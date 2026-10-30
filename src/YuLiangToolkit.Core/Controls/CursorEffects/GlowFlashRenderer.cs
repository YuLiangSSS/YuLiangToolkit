using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.Core.Controls.CursorEffects;

/// <summary>
/// 点击效果「光晕闪烁」的渲染器：点击处一团柔光亮一下、边扩大边淡出（约 0.37 秒）。
///
/// <para>用冻结的径向渐变画刷 + <see cref="DrawingContext.PushOpacity"/> 控制整体透明度，
/// 连点最多保留 6 团；时间单位是 60fps 基准帧（与粒子引擎一致）。</para>
/// </summary>
internal sealed class GlowFlashRenderer
{
    /// <summary>一团闪光持续的基准帧数（约 0.37 秒）。</summary>
    private const double Duration = 22;

    /// <summary>同时最多保留几团。</summary>
    private const int MaxFlashes = 6;

    /// <summary>起始半径（DIP）。</summary>
    private const double StartRadius = 14;

    /// <summary>光团最大半径默认值（= v0.36.x 的固定外观）。</summary>
    public const double DefaultRadius = 64;

    /// <summary>光团最大半径可调下限（主页设置滑块）。</summary>
    public const double MinRadius = 6;

    /// <summary>光团最大半径可调上限。</summary>
    public const double MaxRadius = 400;

    private readonly List<Flash> _flashes = new(MaxFlashes);
    private Brush? _brush;
    private double _radius = DefaultRadius;

    /// <summary>光团最大半径（DIP，主页设置「光晕闪烁半径」滑块；越界自动夹住）。</summary>
    public double Radius
    {
        get => _radius;
        set => _radius = double.IsNaN(value) ? DefaultRadius : Math.Clamp(value, MinRadius, MaxRadius);
    }

    /// <summary>现在有没有还在闪的光团。</summary>
    public bool HasVisual => _flashes.Count > 0;

    /// <summary>按当前特效颜色重建（冻结）画刷。</summary>
    public void SetColor(Color color)
    {
        var center = Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF);
        var middle = Color.FromArgb(0x8C, color.R, color.G, color.B);
        var edge = Color.FromArgb(0x00, color.R, color.G, color.B);

        var brush = new RadialGradientBrush(center, edge)
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        brush.GradientStops.Add(new GradientStop(center, 0));
        brush.GradientStops.Add(new GradientStop(middle, 0.35));
        brush.GradientStops.Add(new GradientStop(edge, 1));
        brush.Freeze();
        _brush = brush;
    }

    /// <summary>在指定位置触发一团闪光。</summary>
    public void Trigger(Point center)
    {
        if (_flashes.Count >= MaxFlashes) _flashes.RemoveAt(0);
        _flashes.Add(new Flash(center));
    }

    /// <summary>按基准帧推进：老化 + 回收。</summary>
    public void Update(double frameSteps)
    {
        if (double.IsNaN(frameSteps) || frameSteps <= 0) return;

        for (var i = _flashes.Count - 1; i >= 0; i--)
        {
            var flash = _flashes[i];
            flash.Age += frameSteps;
            if (flash.Age >= Duration) _flashes.RemoveAt(i);
        }
    }

    /// <summary>清空。</summary>
    public void Clear() => _flashes.Clear();

    /// <summary>画一帧。</summary>
    public void Render(DrawingContext drawingContext)
    {
        if (_brush is null) return;

        for (var i = 0; i < _flashes.Count; i++)
        {
            var flash = _flashes[i];
            var progress = Math.Clamp(flash.Age / Duration, 0, 1);
            if (progress >= 1) continue;

            var eased = 1 - Math.Pow(1 - progress, 2.0);
            var startRadius = Math.Min(StartRadius, _radius * 0.5);   // 半径调得很小时起点也跟着缩
            var radius = startRadius + (_radius - startRadius) * eased;
            var opacity = Math.Pow(1 - progress, 1.6);

            drawingContext.PushOpacity(opacity);
            try
            {
                drawingContext.DrawEllipse(_brush, null, flash.Center, radius, radius);
            }
            finally
            {
                drawingContext.Pop();
            }
        }
    }

    private sealed class Flash
    {
        public Flash(Point center) => Center = center;

        public Point Center { get; }

        public double Age { get; set; }
    }
}
