using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.Core.Controls.CursorEffects;

/// <summary>
/// 点击效果「散光扩散」的渲染器：点击处扩散一圈**彩色色散光环**（参考 Kerxs/glassium 的 dispersion 观感）。
///
/// <para>WPF 没有 GPU 折射，这里用「红 / 绿 / 蓝三圈半径略有差异的细环」模拟色散：
/// 环从点击处向外扩散、同时变淡，中心再留一个很快消失的亮核。全部画刷 / 画笔在静态构造里
/// 建好并冻结，触发时只往一个小列表里加一条状态，绘制时零 new。</para>
///
/// <para>时间单位与粒子引擎一致：60fps 基准帧（<see cref="Update"/> 收真实时间换算后的帧步长）。</para>
/// </summary>
internal sealed class DispersionRippleRenderer
{
    /// <summary>一次扩散持续的基准帧数（约 0.66 秒）。</summary>
    private const double Duration = 40;

    /// <summary>同时最多保留几圈（连点不会无限堆积）。</summary>
    private const int MaxRipples = 8;

    /// <summary>扩散起始半径 / 终止半径（DIP）。</summary>
    private const double StartRadius = 12;
    private const double EndRadius = 98;

    /// <summary>色散三环的颜色与粗细（红/绿/蓝，各偏一点，形成色边）。</summary>
    private static readonly Pen[] SpectralPens =
    {
        CreatePen(Color.FromArgb(0xE6, 0xFF, 0x5C, 0x7A), 1.8),   // 红
        CreatePen(Color.FromArgb(0xE6, 0x5C, 0xFF, 0xA8), 1.8),   // 绿
        CreatePen(Color.FromArgb(0xE6, 0x5C, 0xA8, 0xFF), 1.8),   // 蓝
    };

    /// <summary>内圈一道白色细环（让轮廓更清楚）。</summary>
    private static readonly Pen CorePen = CreatePen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), 1.1);

    private readonly List<Ripple> _ripples = new(MaxRipples);

    /// <summary>现在有没有还在扩散的光环。</summary>
    public bool HasVisual => _ripples.Count > 0;

    /// <summary>在指定位置触发一圈散光（连点会排队，超过上限丢最旧的）。</summary>
    public void Trigger(Point center)
    {
        if (_ripples.Count >= MaxRipples) _ripples.RemoveAt(0);
        _ripples.Add(new Ripple(center));
    }

    /// <summary>按基准帧推进：老化 + 回收。</summary>
    public void Update(double frameSteps)
    {
        if (double.IsNaN(frameSteps) || frameSteps <= 0) return;

        for (var i = _ripples.Count - 1; i >= 0; i--)
        {
            var ripple = _ripples[i];
            ripple.Age += frameSteps;
            if (ripple.Age >= Duration) _ripples.RemoveAt(i);
        }
    }

    /// <summary>清空（鼠标离开 / 失活 / 关掉特效时）。</summary>
    public void Clear() => _ripples.Clear();

    /// <summary>画一帧。<paramref name="coreBrush"/> 是覆盖层调色板里的主色画刷（亮核用）。</summary>
    public void Render(DrawingContext drawingContext, Brush? coreBrush)
    {
        for (var i = 0; i < _ripples.Count; i++)
        {
            var ripple = _ripples[i];
            var progress = Math.Clamp(ripple.Age / Duration, 0, 1);
            if (progress >= 1) continue;

            // 先快后慢的扩散（easeOut），透明度快速衰减
            var eased = 1 - Math.Pow(1 - progress, 2.4);
            var radius = StartRadius + (EndRadius - StartRadius) * eased;
            var fade = Math.Pow(1 - progress, 1.5);

            drawingContext.PushOpacity(fade);
            try
            {
                // 三圈色散：半径分别 -2 / 0 / +2，模拟不同波长折射率不同
                for (var band = 0; band < SpectralPens.Length; band++)
                {
                    var offset = (band - 1) * 2.0 * (0.35 + 0.65 * progress);
                    var r = radius + offset;
                    if (r <= 0) continue;
                    drawingContext.DrawEllipse(null, SpectralPens[band], ripple.Center, r, r);
                }

                // 白色内环 + 亮核（亮核收缩得比光环快）
                var innerRadius = radius * 0.82;
                if (innerRadius > 1)
                {
                    drawingContext.DrawEllipse(null, CorePen, ripple.Center, innerRadius, innerRadius);
                }

                if (coreBrush is not null)
                {
                    var coreRadius = 18 * (1 - progress);
                    if (coreRadius > 0.5)
                    {
                        drawingContext.DrawEllipse(coreBrush, null, ripple.Center, coreRadius, coreRadius);
                    }
                }
            }
            finally
            {
                drawingContext.Pop();
            }
        }
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private sealed class Ripple
    {
        public Ripple(Point center) => Center = center;

        public Point Center { get; }

        public double Age { get; set; }
    }
}
