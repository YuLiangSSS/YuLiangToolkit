using System;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.Live2D.Ai;

namespace YuLiangToolkit.Tools.Live2D.Controls;

/// <summary>
/// 好感度「粉色试管」可视化控件（用户参考图：粉色描边的试管 / 小药瓶，顶部是心形开口，
/// 瓶身圆底、内侧左边几道短刻度，液体按好感度高度从瓶底往上填）。
///
/// <para><b>实现方式</b>：<see cref="FrameworkElement.OnRender"/> 全部用 <see cref="DrawingContext"/>
/// 画出来（不引图片 / 不用 XAML 模板），所以在任何主题、任何缩放下都清晰，也不会多占资源。</para>
///
/// <para><b>不跑定时器</b>：只有 <see cref="Value"/> / <see cref="Mood"/> 真的变了才重绘一次
/// （<see cref="FrameworkPropertyMetadataOptions.AffectsRender"/>），没有动画循环，桌宠卡顿时也不会拖后腿。</para>
///
/// <para><b>比例</b>：固定纵横比 1 : 2.4（<see cref="AspectRatio"/>），把控件放在任意宽高的盒子里
/// 都会等比缩放并居中，不会变形（聊天窗里 42×98、工具页里 72×172 都行）。</para>
///
/// <para><b>设计坐标</b>：内部按 100 × 240 的虚拟画布画（宽 100、高 240），再整体等比缩放到控件尺寸；
/// 下面的常量都是这个虚拟画布里的数字，改比例只动这里。</para>
/// </summary>
public sealed class AffectionVial : FrameworkElement
{
    /// <summary>高 / 宽：参考图约 1 : 2.4（宽 42 时高约 100.8）。</summary>
    public const double AspectRatio = 2.4;

    // ==================== 设计坐标（宽 100 × 高 240） ====================

    /// <summary>虚拟画布宽。</summary>
    private const double DesignWidth = 100;

    /// <summary>虚拟画布高（= 100 × 2.4）。</summary>
    private const double DesignHeight = DesignWidth * AspectRatio;

    /// <summary>瓶身中线。</summary>
    private const double CenterX = DesignWidth / 2;

    /// <summary>顶部心形两个「耳朵」的圆弧半径（心形由左右两个圆 + 中间的缺口组成）。</summary>
    private const double LobeRadius = 28;

    /// <summary>心形圆弧圆心相对中线的水平偏移（左右各一个）。</summary>
    private const double LobeOffset = 22;

    /// <summary>心形圆弧圆心的高度（也是瓶身开始的最宽处）。</summary>
    private const double LobeCenterY = LobeRadius;

    /// <summary>两个圆弧相交形成的心形中缝高度（比圆弧顶部低一点，看起来才像心形开口）。</summary>
    private static readonly double NotchY =
        LobeCenterY - Math.Sqrt((LobeRadius * LobeRadius) - (LobeOffset * LobeOffset)); // ≈ 10.68

    /// <summary>直筒段结束 / 圆底开始的高度（圆底圆心）。</summary>
    private const double StraightBottomY = 190;

    /// <summary>圆底半径（正好半圆，所以瓶子最低点 = 240）。</summary>
    private const double BottomRadius = DesignWidth / 2;

    /// <summary>内壁 / 液体相对外轮廓留的边（不然液体会盖住粉色描边）。</summary>
    private const double InnerInset = 3;

    /// <summary>液体最高处（好感度 100 时的液面；再往上就是心形开口，不填）。</summary>
    private const double LiquidTopY = 30;

    /// <summary>外轮廓描边粗细（设计坐标；会跟着整体一起缩放）。</summary>
    private const double OutlineThickness = 2.2;

    // ==================== 依赖属性 ====================

    /// <summary>
    /// <see cref="Value"/>：好感度 0 ~ 100（默认 50，NaN / 越界会被夹取）。
    /// 绑到这个属性上的值变化时会自动重绘（AffectsRender），别的属性变化不会触发重绘。
    /// </summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(AffectionVial),
        new FrameworkPropertyMetadata(
            50.0,
            FrameworkPropertyMetadataOptions.AffectsRender,
            propertyChangedCallback: null,
            coerceValueCallback: CoerceAffectionValue));

    /// <summary>
    /// <see cref="Mood"/>：情绪档位。只影响配色（心动更粉、生气偏冷灰粉），不改变液面高度。
    /// </summary>
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood),
        typeof(AiMood),
        typeof(AffectionVial),
        new FrameworkPropertyMetadata(AiMood.Normal, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>好感度 0 ~ 100（默认 50）。改这个值才会重绘液体高度。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>情绪档位（心动 / 开心 / 平常 / 闹别扭 / 生气）：影响液体与描边配色。</summary>
    public AiMood Mood
    {
        get => (AiMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    // ==================== 几何缓存（一次建好、冻结，不随重绘重复构造） ====================

    /// <summary>瓶身外轮廓：心形顶部 + 直筒 + 圆底。</summary>
    private static readonly StreamGeometry SilhouetteGeometry = BuildSilhouette();

    /// <summary>液体裁剪区：比外轮廓往里缩 <see cref="InnerInset"/> 的圆底直筒。</summary>
    private static readonly StreamGeometry InnerClipGeometry = BuildInnerClip();

    /// <summary>心形开口（顶部内壁那一圈心形线）。</summary>
    private static readonly StreamGeometry OpeningGeometry = BuildOpeningHeart();

    /// <summary>瓶身玻璃底色（极淡的粉，给透明桌面上的深色聊天窗用）。</summary>
    private static readonly SolidColorBrush GlassBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0x8F, 0xB8)));

    /// <summary>心形开口的填充（一点点白，像凹进去的玻璃壁）。</summary>
    private static readonly SolidColorBrush OpeningFillBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));

    /// <summary>心形开口的描边（浅粉）。</summary>
    private static readonly SolidColorBrush OpeningStrokeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xD0, 0xDF)));

    /// <summary>刻度线颜色（深粉半透明）。</summary>
    private static readonly SolidColorBrush TickBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0x6F, 0xA3)));

    /// <summary>液面高光颜色。</summary>
    private static readonly SolidColorBrush SurfaceHighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)));

    /// <summary>气泡颜色（半透明白）。</summary>
    private static readonly SolidColorBrush BubbleBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));

    /// <summary>装饰气泡的固定位置（X 是设计坐标，Y 是相对液面的高度比例：0 = 液面，1 = 瓶底）。</summary>
    private static readonly (double X, double Y, double Radius)[] Bubbles =
    {
        (34, 0.26, 2.6),
        (62, 0.50, 1.9),
        (44, 0.74, 2.2),
    };

    // ==================== 公开的纯函数（自检用） ====================

    /// <summary>
    /// 把任意好感度夹到 0 ~ 100 并保留 1 位小数。NaN / 无穷当作默认好感度 50（界面不会因为绑定失败画成空瓶）。
    /// </summary>
    public static double ClampValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 50.0;
        return Math.Round(Math.Clamp(value, 0.0, 100.0), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 好感度 → 液体高度比例（0 = 空瓶，1 = 满瓶），越界自动夹取；自检断言用。
    /// <c>LiquidRatio(0) == 0</c>、<c>LiquidRatio(50) == 0.5</c>、<c>LiquidRatio(100) == 1</c>。
    /// </summary>
    public static double LiquidRatio(double value) => ClampValue(value) / 100.0;

    // ==================== 布局 / 绘制 ====================

    /// <summary>没有显式宽高时按 1 : 2.4 给一个默认尺寸（42 × 100.8），被父容器限制时等比缩小。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? 42.0
            : Math.Min(availableSize.Width, 42.0);

        var height = width * AspectRatio;
        if (!double.IsInfinity(availableSize.Height) && availableSize.Height > 0 && height > availableSize.Height)
        {
            height = availableSize.Height;
            width = height / AspectRatio;
        }

        return new Size(width, height);
    }

    /// <summary>画整支试管：外轮廓（粉色描边）→ 液体（按好感度填充）→ 玻璃反光 → 心形开口 → 刻度。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 6 || height < 6) return;

        // 固定纵横比 1 : 2.4：在可用区域里等比缩放并居中；四周留 1px，避免描边顶到控件边界被裁掉。
        var vialWidth = Math.Min(width - 2, (height - 2) / AspectRatio);
        if (vialWidth <= 2) return;
        var vialHeight = vialWidth * AspectRatio;
        var scale = vialWidth / DesignWidth;
        var left = (width - vialWidth) / 2;
        var top = (height - vialHeight) / 2;

        dc.PushTransform(new TranslateTransform(left, top));
        dc.PushTransform(new ScaleTransform(scale, scale));
        try
        {
            DrawVial(dc);
        }
        finally
        {
            dc.Pop();
            dc.Pop();
        }
    }

    /// <summary>在设计坐标系（100 × 240）里绘制。</summary>
    private void DrawVial(DrawingContext dc)
    {
        var ratio = LiquidRatio(Value);
        var (surface, mid, deep) = LiquidPalette(Mood);

        // 1) 瓶身：极淡粉色玻璃 + 粉色描边（参考图就是粉色线稿的小试管）
        dc.DrawGeometry(GlassBrush, new Pen(new SolidColorBrush(PaletteOutline(Mood)), OutlineThickness), SilhouetteGeometry);

        // 2) 液体：裁剪在内壁里，从瓶底往上按好感度高度填充
        var liquidTop = DesignHeight - (ratio * (DesignHeight - LiquidTopY));
        if (ratio > 0.001 && liquidTop < DesignHeight - 0.5)
        {
            dc.PushClip(InnerClipGeometry);

            // 渐变相对液体矩形：液面最浅、越往下越深（每次现建小画刷，只在值变化时执行，开销可忽略）
            var liquidBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(surface, 0.0),
                    new GradientStop(mid, 0.55),
                    new GradientStop(deep, 1.0),
                },
            };
            dc.DrawRectangle(liquidBrush, null, new Rect(0, liquidTop, DesignWidth, DesignHeight - liquidTop));

            // 液面高光：一条圆润的亮色椭圆（参考图里液面上缘的亮边）
            dc.DrawEllipse(SurfaceHighlightBrush, null, new Point(CenterX, liquidTop), (DesignWidth / 2) - InnerInset - 1, 3.6);

            // 装饰小气泡：固定位置（不做动画），液体太浅时自然被圆底裁掉
            foreach (var (x, y, radius) in Bubbles)
            {
                var bubbleY = liquidTop + ((DesignHeight - liquidTop) * y);
                if (bubbleY + radius < DesignHeight - 2)
                {
                    dc.DrawEllipse(BubbleBrush, null, new Point(x, bubbleY), radius, radius);
                }
            }

            dc.Pop();
        }

        // 3) 心形开口：顶部内壁再描一圈心形（比外轮廓缩进 4），读起来就是「心形开口」
        dc.DrawGeometry(OpeningFillBrush, new Pen(OpeningStrokeBrush, 1.4), OpeningGeometry);

        // 4) 内侧左边 4 道短刻度线（20% / 40% / 60% / 80% 高度，越靠近瓶底越收一点，贴着内壁）
        var tickPen = new Pen(TickBrush, 1.5);
        for (var i = 1; i <= 4; i++)
        {
            var tickRatio = i / 5.0;
            var y = DesignHeight - (tickRatio * (DesignHeight - LiquidTopY));
            var innerLeft = InnerLeftAt(y);
            dc.DrawLine(tickPen, new Point(innerLeft + 3, y), new Point(innerLeft + 17, y));
        }
    }

    /// <summary>某一高度处内壁的左边界 X（直筒段固定、圆底段按圆弧收窄），刻度线贴着它画。</summary>
    private static double InnerLeftAt(double y)
    {
        var innerRadius = BottomRadius - InnerInset;
        if (y <= StraightBottomY) return CenterX - innerRadius;

        var dy = Math.Min(y - StraightBottomY, innerRadius);
        return CenterX - Math.Sqrt(Math.Max(0, (innerRadius * innerRadius) - (dy * dy)));
    }

    // ==================== 配色 ====================

    /// <summary>按情绪挑液体配色（液面 → 中间 → 瓶底）：心动最粉、生气偏冷灰粉。</summary>
    private static (Color Surface, Color Mid, Color Deep) LiquidPalette(AiMood mood) => mood switch
    {
        AiMood.Love => (
            Color.FromArgb(0xFF, 0xFF, 0xE4, 0xEE),
            Color.FromArgb(0xFF, 0xFF, 0x9C, 0xC2),
            Color.FromArgb(0xFF, 0xFF, 0x63, 0x9B)),
        AiMood.Happy => (
            Color.FromArgb(0xFF, 0xFF, 0xE8, 0xF1),
            Color.FromArgb(0xFF, 0xFF, 0xB3, 0xCD),
            Color.FromArgb(0xFF, 0xFF, 0x8F, 0xB8)),
        AiMood.Sulky => (
            Color.FromArgb(0xFF, 0xF4, 0xE7, 0xEE),
            Color.FromArgb(0xFF, 0xD9, 0xB4, 0xC6),
            Color.FromArgb(0xFF, 0xC2, 0x94, 0xAA)),
        AiMood.Angry => (
            Color.FromArgb(0xFF, 0xEA, 0xE2, 0xE8),
            Color.FromArgb(0xFF, 0xC6, 0xB2, 0xBE),
            Color.FromArgb(0xFF, 0xA1, 0x8D, 0x9B)),
        _ => (
            Color.FromArgb(0xFF, 0xFD, 0xF2, 0xF7),
            Color.FromArgb(0xFF, 0xFF, 0xC6, 0xDA),
            Color.FromArgb(0xFF, 0xFF, 0xA6, 0xC7)),
    };

    /// <summary>描边颜色：心动 / 开心用亮粉，生气偏冷灰粉（和液体配色对应）。</summary>
    private static Color PaletteOutline(AiMood mood) => mood switch
    {
        AiMood.Love => Color.FromRgb(0xFF, 0x5F, 0x97),
        AiMood.Happy => Color.FromRgb(0xFF, 0x6F, 0xA3),
        AiMood.Sulky => Color.FromRgb(0xC8, 0x92, 0xAB),
        AiMood.Angry => Color.FromRgb(0xA9, 0x8C, 0x9B),
        _ => Color.FromRgb(0xFF, 0x8F, 0xB8),
    };

    // ==================== 几何构造（静态一次） ====================

    /// <summary>
    /// 瓶身外轮廓：从左腰开始 → 顶部心形左耳圆弧 → 心形中缝 → 右耳圆弧 → 右侧直筒 →
    /// 圆底（半圆）→ 左侧直筒回到起点。
    /// </summary>
    private static StreamGeometry BuildSilhouette()
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, LobeCenterY), isFilled: true, isClosed: true);

            // 左耳：从左腰沿圆弧翻到顶部中缝（参考系 y 轴朝下，所以屏幕上是顺时针）
            ctx.ArcTo(new Point(CenterX, NotchY), new Size(LobeRadius, LobeRadius), 0,
                isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);

            // 右耳：从中缝翻到右腰
            ctx.ArcTo(new Point(DesignWidth, LobeCenterY), new Size(LobeRadius, LobeRadius), 0,
                isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);

            // 右直筒 → 圆底（半圆，半径 50，最低点正好落在 240）
            ctx.LineTo(new Point(DesignWidth, StraightBottomY), isStroked: true, isSmoothJoin: true);
            ctx.ArcTo(new Point(0, StraightBottomY), new Size(BottomRadius, BottomRadius), 0,
                isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);

            // 左直筒（收口由 BeginFigure 的 IsClosed 自动连回起点）
            ctx.LineTo(new Point(0, LobeCenterY), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 液体裁剪区：外轮廓整体往里缩 <see cref="InnerInset"/> 的「直筒 + 圆底」，顶部封在液体最高处。
    /// <para>⚠ 圆底弧必须 <see cref="SweepDirection.Counterclockwise"/>：起点在左、终点在右，
    /// 逆时针才会从瓶底绕过去；写成顺时针会往瓶身里凹一个圆洞（液体中间出现一块黑色空缺）。</para>
    /// </summary>
    private static StreamGeometry BuildInnerClip()
    {
        var innerRadius = BottomRadius - InnerInset;
        var left = CenterX - innerRadius;
        var right = CenterX + innerRadius;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(left, LiquidTopY), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(left, StraightBottomY), isStroked: false, isSmoothJoin: true);
            ctx.ArcTo(new Point(right, StraightBottomY), new Size(innerRadius, innerRadius), 0,
                isLargeArc: false, SweepDirection.Counterclockwise, isStroked: false, isSmoothJoin: true);
            ctx.LineTo(new Point(right, LiquidTopY), isStroked: false, isSmoothJoin: true);
            // 顶边由 IsClosed 自动连回，液体永远不会漫到心形开口里
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>顶部心形开口：用比外轮廓小 4 的同圆心圆弧 + 向下的尖，画成一个完整的心。</summary>
    private static StreamGeometry BuildOpeningHeart()
    {
        const double inset = 4;
        var radius = LobeRadius - inset;                              // 24
        var notchY = LobeCenterY - Math.Sqrt((radius * radius) - (LobeOffset * LobeOffset)); // ≈ 18.4
        var left = CenterX - LobeOffset - radius;                      // 4
        var right = CenterX + LobeOffset + radius;                     // 96
        var tipY = LobeCenterY + radius;                               // 52 附近（心尖）

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(left, LobeCenterY), isFilled: true, isClosed: true);
            ctx.ArcTo(new Point(CenterX, notchY), new Size(radius, radius), 0,
                isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
            ctx.ArcTo(new Point(right, LobeCenterY), new Size(radius, radius), 0,
                isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
            ctx.LineTo(new Point(CenterX, tipY), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>好感度值的变化钩子：把越界值夹回 0 ~ 100（NaN 当默认 50），并提供给自检断言。</summary>
    private static object CoerceAffectionValue(DependencyObject d, object baseValue)
        => baseValue is double value ? ClampValue(value) : 50.0;

    /// <summary>把画刷冻结（跨线程 / 多次重绘都安全，也省一点内存管理开销）。</summary>
    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
