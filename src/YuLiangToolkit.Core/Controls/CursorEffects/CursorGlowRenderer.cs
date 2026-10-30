using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.Core.Controls.CursorEffects;

/// <summary>
/// 自动效果「鼠标散光」的渲染器：鼠标位置一圈**微弱的圆形发散光**，带极轻微的呼吸脉动。
///
/// <para>只用两三个冻结的径向渐变画刷 + <see cref="DrawingContext.DrawEllipse"/>，
/// 没有每帧 new 画刷；颜色跟着「粒子颜色」走，所以玻璃主题 / 深色主题下都协调。</para>
///
/// <para>本类由 <c>ParticleOverlay</c> 持有；位置每帧由覆盖层写入 <see cref="Center"/>。</para>
/// </summary>
internal sealed class CursorGlowRenderer
{
    /// <summary>光晕半径（DIP）默认值（= v0.36.x 的固定外观）。</summary>
    public const double DefaultRadius = 46;

    /// <summary>光晕半径可调下限（主页设置滑块）。</summary>
    public const double MinRadius = 8;

    /// <summary>光晕半径可调上限。</summary>
    public const double MaxRadius = 400;

    /// <summary>呼吸脉动的幅度（±5%，太强会像闪烁）。</summary>
    private const double PulseAmplitude = 0.05;

    /// <summary>呼吸脉动速度（弧度/秒）。</summary>
    private const double PulseSpeed = 2.2;

    private Brush? _softBrush;
    private Brush? _coreBrush;
    private double _radius = DefaultRadius;

    /// <summary>光晕半径（DIP，主页设置「鼠标散光半径」滑块；越界自动夹住）。</summary>
    public double Radius
    {
        get => _radius;
        set => _radius = double.IsNaN(value) ? DefaultRadius : Math.Clamp(value, MinRadius, MaxRadius);
    }

    /// <summary>要不要画（为 false 时 <see cref="Render"/> 什么都不做）。</summary>
    public bool Visible { get; set; }

    /// <summary>光晕圆心（控件坐标）。</summary>
    public Point Center { get; set; }

    /// <summary>按当前特效颜色重建（冻结）画刷；颜色切换时调用一次，不是每帧。</summary>
    public void SetColor(Color color)
    {
        // 外圈：很淡的圆晕（中心 alpha 0x38，边缘到 0）
        _softBrush = BuildRadial(
            Color.FromArgb(0x38, color.R, color.G, color.B),
            Color.FromArgb(0x12, color.R, color.G, color.B),
            Color.FromArgb(0x00, color.R, color.G, color.B));

        // 内圈：稍亮一点的小光核，让“圆形”看得出来
        _coreBrush = BuildRadial(
            Color.FromArgb(0x5C, 255, 255, 255),
            Color.FromArgb(0x1C, color.R, color.G, color.B),
            Color.FromArgb(0x00, color.R, color.G, color.B));
    }

    /// <summary>画一帧（<paramref name="seconds"/> 是累计秒数，用于呼吸脉动）。</summary>
    public void Render(DrawingContext drawingContext, double seconds)
    {
        if (!Visible || _softBrush is null) return;

        var pulse = 1 + PulseAmplitude * Math.Sin(seconds * PulseSpeed);
        var radius = Radius * pulse;

        drawingContext.DrawEllipse(_softBrush, null, Center, radius, radius);
        if (_coreBrush is not null)
        {
            var inner = radius * 0.42;
            drawingContext.DrawEllipse(_coreBrush, null, Center, inner, inner);
        }
    }

    private static RadialGradientBrush BuildRadial(Color center, Color middle, Color edge)
    {
        var brush = new RadialGradientBrush(center, edge)
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        brush.GradientStops.Add(new GradientStop(center, 0));
        brush.GradientStops.Add(new GradientStop(middle, 0.45));
        brush.GradientStops.Add(new GradientStop(edge, 1));
        brush.Freeze();
        return brush;
    }
}
