using System;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.Live2D.Play;

namespace YuLiangToolkit.Tools.Live2D.Controls;

/// <summary>
/// 「大白饭团」外观控件（参考图四种形状：三角形 / 球形 / 太鼓形 / 俵形）。
///
/// <para><b>实现方式</b>：跟 <see cref="AffectionVial"/> 一样全部用 <see cref="DrawingContext"/> 现画
/// （不引图片、不用 XAML 模板）：暖白色米饭底色（径向渐变）+ 边缘浅阴影 + 表面固定种子的米粒颗粒 +
/// 底部柔和投影，所以任何缩放、任何主题下都清晰，也不会多占资源。</para>
///
/// <para><b>不跑定时器</b>：只有 <see cref="Kind"/> 变化（<see cref="FrameworkPropertyMetadataOptions.AffectsRender"/>）
/// 或控件尺寸变化才重绘一次；<see cref="RotationDegrees"/> 只改内部的 <see cref="RotateTransform"/>，
/// 不触发 <see cref="OnRender"/>，滚动 / 被拖动时每帧改角度也不心疼。</para>
///
/// <para><b>比例</b>：内部按 60 × 60 的虚拟画布画（<see cref="DesignSize"/>），再整体等比缩放到控件尺寸；
/// 投喂层按物理半径给它设置 Width = Height = 2 × Radius，所以视觉大小和碰撞半径基本一致。</para>
/// </summary>
public sealed class RiceBallVisual : FrameworkElement
{
    /// <summary>内部虚拟画布边长（DIP）：所有形状 / 米粒坐标都按这个画布写。</summary>
    public const double DesignSize = 60;

    /// <summary>虚拟画布中心。</summary>
    private const double Center = DesignSize / 2;

    // ==================== 依赖属性 ====================

    /// <summary>饭团形状（决定画成三角 / 球 / 太鼓 / 俵形；变化时重绘）。</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(RiceBallKind),
        typeof(RiceBallVisual),
        new FrameworkPropertyMetadata(RiceBallKind.Triangle, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// 旋转角度（度）。只更新渲染变换（绕控件中心转），不触发 <see cref="OnRender"/>；
    /// 由投喂层每帧写一次（撞墙 / 被拖着甩都靠它）。
    /// </summary>
    public static readonly DependencyProperty RotationDegreesProperty = DependencyProperty.Register(
        nameof(RotationDegrees),
        typeof(double),
        typeof(RiceBallVisual),
        new FrameworkPropertyMetadata(0.0, OnRotationDegreesChanged));

    /// <summary>饭团形状（默认三角形）。</summary>
    public RiceBallKind Kind
    {
        get => (RiceBallKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>旋转角度（度，绕中心；只影响观感）。</summary>
    public double RotationDegrees
    {
        get => (double)GetValue(RotationDegreesProperty);
        set => SetValue(RotationDegreesProperty, value);
    }

    // ==================== 渲染变换 ====================

    /// <summary>绕控件中心旋转的变换（常驻一个，改角度不重建）。</summary>
    private readonly RotateTransform _rotation = new();

    /// <summary>创建控件（不抢鼠标：投喂层在 Canvas 层做命中判定）。</summary>
    public RiceBallVisual()
    {
        RenderTransform = _rotation;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = false;
    }

    /// <summary>尺寸变了要按新比例重画一次（缩放跟随物理半径）。</summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    /// <summary>旋转角度变了只改变换（不重画）。</summary>
    private static void OnRotationDegreesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RiceBallVisual visual && e.NewValue is double degrees)
        {
            visual._rotation.Angle = degrees;
        }
    }

    // ==================== 画刷 / 画笔 / 几何（一次建好、冻结） ====================

    /// <summary>米饭主体：暖白径向渐变（左上偏亮，边缘偏米黄）。</summary>
    private static readonly RadialGradientBrush RiceBrush = CreateRiceBrush();

    /// <summary>太鼓形底下那圈「厚度」用的稍深米色（画在主体上再裁剪）。</summary>
    private static readonly SolidColorBrush TaikoRimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x3A, 0xC7, 0xB2, 0x8E)));

    /// <summary>太鼓形顶面（比主体再亮一点，边沿才看得出「厚圆盘」）。</summary>
    private static readonly RadialGradientBrush TaikoTopBrush = CreateTaikoTopBrush();

    /// <summary>轮廓描边：很淡的米黄，给透明底一点边界感。</summary>
    private static readonly Pen RiceEdgePen = FreezePen(new Pen(
        Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xA0, 0x8A, 0x6A))), 1.0));

    /// <summary>米粒颗粒：略深的暖灰（像米粒的水分 / 阴影）。</summary>
    private static readonly SolidColorBrush GrainShadowBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x4A, 0x9A, 0x84, 0x62)));

    /// <summary>米粒颗粒：亮白高光。</summary>
    private static readonly SolidColorBrush GrainHighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)));

    /// <summary>顶部柔光（米饭看起来更「新鲜」）。</summary>
    private static readonly RadialGradientBrush TopGlowBrush = CreateTopGlowBrush();

    /// <summary>底部投影（柔和的椭圆，随形状换位置 / 宽度）。</summary>
    private static readonly RadialGradientBrush ShadowBrush = CreateShadowBrush();

    /// <summary>三角形（圆润的饭团三角）。</summary>
    private static readonly Geometry TriangleGeometry = BuildTriangle();

    /// <summary>球形（圆滚滚）。</summary>
    private static readonly Geometry SphereGeometry = BuildSphere();

    /// <summary>太鼓形（厚圆盘：比球形扁一点）。</summary>
    private static readonly Geometry TaikoGeometry = BuildTaiko();

    /// <summary>俵形（横放的小米袋 / 圆柱，圆角胶囊）。</summary>
    private static readonly Geometry BaleGeometry = BuildBale();

    /// <summary>太鼓形顶面几何（比底形整体上移一点，露出底下的厚度）。</summary>
    private static readonly Geometry TaikoTopGeometry = BuildTaikoTop();

    /// <summary>每种形状固定的米粒坐标（固定种子：每次重绘都长一样，不会闪）。</summary>
    private static readonly Grain[] TriangleGrains = BuildGrains(RiceBallKind.Triangle);

    private static readonly Grain[] SphereGrains = BuildGrains(RiceBallKind.Sphere);

    private static readonly Grain[] TaikoGrains = BuildGrains(RiceBallKind.Taiko);

    private static readonly Grain[] BaleGrains = BuildGrains(RiceBallKind.Bale);

    /// <summary>一颗米粒（坐标 / 半径 / 倾角 / 是否高光）。</summary>
    private readonly record struct Grain(double X, double Y, double RadiusX, double RadiusY, double Angle, bool Highlight);

    // ==================== 绘制 ====================

    /// <summary>把 60 × 60 画布缩放到控件尺寸后逐层画（投影 → 主体 → 米粒 / 高光）。</summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2) return;

        var kind = Kind;

        drawingContext.PushTransform(new ScaleTransform(width / DesignSize, height / DesignSize));
        try
        {
            var geometry = GetGeometry(kind);

            // ① 底部柔和投影（先画在球下面）
            DrawShadow(drawingContext, kind);

            // ② 米饭主体（暖白渐变 + 很淡的描边）
            drawingContext.DrawGeometry(RiceBrush, RiceEdgePen, geometry);

            // ③ 太鼓形：再叠一层亮一点的「顶面」，下沿露出深一点的厚度
            if (kind == RiceBallKind.Taiko)
            {
                drawingContext.PushClip(geometry);
                drawingContext.DrawGeometry(TaikoRimBrush, null, TaikoTopGeometry);
                drawingContext.DrawGeometry(TaikoTopBrush, null, TaikoTopGeometry);
                drawingContext.Pop();
            }

            // ④ 米粒颗粒 + 顶部柔光（裁剪在形状内部，不会画到外面）
            drawingContext.PushClip(geometry);
            DrawGrains(drawingContext, GetGrains(kind));
            DrawTopGlow(drawingContext);
            drawingContext.Pop();
        }
        finally
        {
            drawingContext.Pop();
        }
    }

    /// <summary>形状 → 几何（未知按三角形）。</summary>
    private static Geometry GetGeometry(RiceBallKind kind) => kind switch
    {
        RiceBallKind.Sphere => SphereGeometry,
        RiceBallKind.Taiko => TaikoGeometry,
        RiceBallKind.Bale => BaleGeometry,
        _ => TriangleGeometry,
    };

    /// <summary>形状 → 固定米粒数组。</summary>
    private static Grain[] GetGrains(RiceBallKind kind) => kind switch
    {
        RiceBallKind.Sphere => SphereGrains,
        RiceBallKind.Taiko => TaikoGrains,
        RiceBallKind.Bale => BaleGrains,
        _ => TriangleGrains,
    };

    /// <summary>底部投影：每种形状下面垫一个半透明椭圆（三角形的底边位置更靠上）。</summary>
    private static void DrawShadow(DrawingContext drawingContext, RiceBallKind kind)
    {
        var (centerY, radiusX) = kind switch
        {
            RiceBallKind.Sphere => (54.5, 17.0),
            RiceBallKind.Taiko => (53.0, 20.5),
            RiceBallKind.Bale => (49.0, 22.0),
            _ => (52.0, 19.0),
        };
        var geometry = new EllipseGeometry(new Point(Center, centerY), radiusX, 4.6);
        drawingContext.DrawGeometry(ShadowBrush, null, geometry);
    }

    /// <summary>米粒颗粒：固定坐标 + 轻微倾斜的小椭圆（一半略深、一半亮白，像米饭表面的颗粒）。</summary>
    private static void DrawGrains(DrawingContext drawingContext, Grain[] grains)
    {
        foreach (var grain in grains)
        {
            drawingContext.PushTransform(new RotateTransform(grain.Angle, grain.X, grain.Y));
            drawingContext.DrawEllipse(
                grain.Highlight ? GrainHighlightBrush : GrainShadowBrush,
                null,
                new Point(grain.X, grain.Y),
                grain.RadiusX,
                grain.RadiusY);
            drawingContext.Pop();
        }
    }

    /// <summary>顶部柔光：左上方一块很淡的白，让饭团看起来有体积。</summary>
    private static void DrawTopGlow(DrawingContext drawingContext)
    {
        var glow = new EllipseGeometry(new Point(21.5, 18.5), 12.5, 9.0);
        drawingContext.DrawGeometry(TopGlowBrush, null, glow);
    }

    // ==================== 几何 / 画刷构造（全部冻结，避免每帧重新构造） ====================

    /// <summary>暖白米饭径向渐变（中心偏左上）。</summary>
    private static RadialGradientBrush CreateRiceBrush()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.36, 0.30),
            GradientOrigin = new Point(0.36, 0.30),
            RadiusX = 0.86,
            RadiusY = 0.86,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xFE, 0xF9), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFD, 0xF7, 0xE9), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF3, 0xE9, 0xD5), 0.78));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE6, 0xD8, 0xBC), 1.0));
        return Freeze(brush);
    }

    /// <summary>太鼓形顶面渐变（比主体更亮一点）。</summary>
    private static RadialGradientBrush CreateTaikoTopBrush()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.40, 0.34),
            GradientOrigin = new Point(0.40, 0.34),
            RadiusX = 0.80,
            RadiusY = 0.80,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFB), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFD, 0xF8, 0xEC), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xF0, 0xE4, 0xCB), 1.0));
        return Freeze(brush);
    }

    /// <summary>顶部柔光渐变（白 → 透明）。</summary>
    private static RadialGradientBrush CreateTopGlowBrush()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
        return Freeze(brush);
    }

    /// <summary>底部投影渐变（半透明黑 → 透明）。</summary>
    private static RadialGradientBrush CreateShadowBrush()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0x00, 0x00, 0x00), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1.0));
        return Freeze(brush);
    }

    /// <summary>三角形：三个角都做圆角，看起来是捏出来的饭团而不是纸片。</summary>
    private static StreamGeometry BuildTriangle()
        => BuildRoundedPolygon(new[]
        {
            new Point(Center, 4.0),
            new Point(5.5, 52.0),
            new Point(54.5, 52.0),
        }, 11.0);

    /// <summary>球形：正圆（半径 27.5，留 2.5 给投影 / 描边）。</summary>
    private static Geometry BuildSphere()
        => Freeze(new EllipseGeometry(new Point(Center, Center), 27.5, 27.5));

    /// <summary>太鼓形：略扁的圆盘（横向 28.5、纵向 24）。</summary>
    private static Geometry BuildTaiko()
        => Freeze(new EllipseGeometry(new Point(Center, 30.6), 28.5, 24.0));

    /// <summary>太鼓形顶面：比底形整体上移 4，底边露出一圈厚度。</summary>
    private static Geometry BuildTaikoTop()
        => Freeze(new EllipseGeometry(new Point(Center, 26.6), 27.0, 22.8));

    /// <summary>俵形：横放的小米袋（圆角胶囊；圆角半径 = 高度的一半）。</summary>
    private static Geometry BuildBale()
        => Freeze(new RectangleGeometry(new Rect(4.0, 13.0, 52.0, 34.0), 17.0, 17.0));

    /// <summary>
    /// 按顶点列表生成圆角多边形：每个角按内角算切线距离（radius / tan(内角/2)），
    /// 再用 <see cref="StreamGeometryContext.ArcTo"/> 画真正的圆弧 —— 三角形饭团三个角就是圆的。
    /// </summary>
    private static StreamGeometry BuildRoundedPolygon(Point[] vertices, double radius)
    {
        var count = vertices.Length;
        var starts = new Point[count];
        var ends = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var previous = vertices[(i - 1 + count) % count];
            var current = vertices[i];
            var next = vertices[(i + 1) % count];

            var toPrevious = Normalize(current, previous);
            var toNext = Normalize(current, next);
            var dot = Math.Clamp((toPrevious.X * toNext.X) + (toPrevious.Y * toNext.Y), -1, 1);
            var interior = Math.Acos(dot);   // 内角（0 ~ π）
            var tangent = interior > 0.05 ? radius / Math.Tan(interior / 2) : radius;
            var maxDistance = Math.Min(Distance(current, previous), Distance(current, next)) * 0.45;
            var offset = Math.Min(tangent, maxDistance);

            starts[i] = new Point(current.X + (toPrevious.X * offset), current.Y + (toPrevious.Y * offset));
            ends[i] = new Point(current.X + (toNext.X * offset), current.Y + (toNext.Y * offset));
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            // 顶点顺序在屏幕坐标（y 向下）里是顺时针还是逆时针，决定圆角圆弧的转向
            var sweep = SignedArea(vertices) > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

            context.BeginFigure(starts[0], isFilled: true, isClosed: true);
            for (var i = 0; i < count; i++)
            {
                // ① 绕过顶点 i 的小圆弧（从进入侧的切点 → 离开侧的切点）
                context.ArcTo(ends[i], new Size(radius, radius), 0, isLargeArc: false,
                    sweep, isStroked: true, isSmoothJoin: false);
                // ② 沿着边走到下一个顶点的圆角起点
                context.LineTo(starts[(i + 1) % count], isStroked: true, isSmoothJoin: false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>多边形有向面积（屏幕坐标 y 向下：正 = 视觉顺时针，负 = 视觉逆时针）。</summary>
    private static double SignedArea(Point[] vertices)
    {
        var sum = 0.0;
        for (var i = 0; i < vertices.Length; i++)
        {
            var current = vertices[i];
            var next = vertices[(i + 1) % vertices.Length];
            sum += (current.X * next.Y) - (next.X * current.Y);
        }
        return sum / 2;
    }

    /// <summary>单位方向向量（两点重合时返回零向量）。</summary>
    private static Vector Normalize(Point from, Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        return length < 0.0001 ? new Vector(0, 0) : new Vector(dx / length, dy / length);
    }

    /// <summary>两点距离。</summary>
    private static double Distance(Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// 米粒坐标：按形状固定种子生成（<c>20261108 + kind * 7919</c>），
    /// 同一个形状每次画的颗粒位置完全一致 —— 不会每帧乱跳。
    /// </summary>
    private static Grain[] BuildGrains(RiceBallKind kind)
    {
        var random = new Random(20261108 + ((int)kind * 7919));
        const int count = 16;
        var grains = new Grain[count];
        for (var i = 0; i < count; i++)
        {
            grains[i] = new Grain(
                X: 8 + (random.NextDouble() * 44),
                Y: 7 + (random.NextDouble() * 46),
                RadiusX: 0.7 + (random.NextDouble() * 0.9),
                RadiusY: 0.45 + (random.NextDouble() * 0.5),
                Angle: (random.NextDouble() * 180) - 90,
                Highlight: random.NextDouble() < 0.35);
        }
        return grains;
    }

    /// <summary>冻结画刷（跨线程 / 复用安全）。</summary>
    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>冻结画笔（含内部画刷）。</summary>
    private static Pen FreezePen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
