using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Effects;

namespace YuLiangToolkit.Core.Controls.CursorEffects;

/// <summary>
/// 点击效果「反色圆环」的渲染器：点击处出现一块**颜色反转**的圆形区域，先快速扩散、再收缩到看不见
/// （时间线与曲线见 <see cref="InvertCircleAnimation"/>）。
///
/// <para><b>为什么是「取景 + 逐像素反色」</b>：WPF 没有 CSS 的 <c>mix-blend-mode: difference</c>，
/// 也没有内置的「反色」效果（像素着色器要 fxc 编译、项目里不引第三方包），所以这里把
/// 「窗口内容根节点」的一小块（反色圆的最大外接正方形）用 <see cref="VisualBrush"/> 渲染进
/// <see cref="RenderTargetBitmap"/>，逐像素取反后放进 <see cref="WriteableBitmap"/>，再按当前半径用
/// 径向渐变不透明度蒙版画出来。取景源**不含特效覆盖层本身**（XAML 里两者是兄弟节点），否则会把上一帧
/// 的反色 / 粒子也取进去、越叠越花。</para>
///
/// <para><b>为什么只在触发时取景（v0.36.0 的关键取舍）</b>：WPF 的 <see cref="VisualBrush"/> 取景会
/// 把整棵内容树软件渲染一遍（实测 1280×820 的窗口约 20~35ms，取景区域设小也省不下来），
/// 每帧取景会把 60fps 动画拖成 20~30fps、还持续卡 UI 线程。所以：
/// <list type="number">
///   <item>鼠标按下时取景**一次**（约一帧的开销），之后整段动画只改蒙版半径，稳定 60fps；</item>
///   <item>鼠标松开后（导航 / 勾选等点击动作通常在这时改内容）**再补取一次景**，保证反色区域跟着新页面走；</item>
///   <item>窗口尺寸变化时直接结束所有圈（缩放后旧取景的比例已经不对了）。</item>
/// </list>
/// 这样「点导航卡片 → 反色的是新页面」这类常见路径不会露出旧画面的反色残影，代价只有点击时约一帧的取景。</para>
///
/// <para><b>可以连点、同时播多圈</b>（v0.37.1）：每在鼠标位置触发一次就新起一圈，**不会**再把上一圈清掉；
/// 每圈各自持有触发瞬间的取景位图、各自走自己的时间线（所以两圈重叠时看到的是两次点击各自的画面）。
/// 为了不无限堆积，同时最多保留 <see cref="MaxCircles"/> 圈，超过时丢最旧的一圈（安全阀，正常连点几乎碰不到）。
/// 鼠标松开只给**最近这一圈**补取景 —— 更早的圈已经播到一半，再换图会闪一下。</para>
///
/// <para><b>两圈重叠 → 变回正色</b>（v0.37.2，<see cref="OverlapCancels"/>，设置页可关）：反色是「取反」，
/// 同一片区域反两次就应该回到原色（等价于对覆盖次数取模 2）。所以每圈除了反色位图，还留一份**正色**位图；
/// 画的时候按「这一圈之前被覆盖了几次」分区域：覆盖次数为偶数的区域画反色（取反），奇数区域画正色（再反一次）。
/// 区域用 <see cref="CombinedGeometry"/> 的 Xor / Exclude / Intersect 逐圈递推，3 圈及以上也保持奇偶正确。
/// 关掉这个开关就退回 v0.37.1 的行为：后触发的圆直接盖住先前的（重叠处仍是反色）。</para>
///
/// <para>窗口底色由覆盖层的 <c>CaptureBackground</c> 补上（内容根节点自己是透明的，不补的话反色区域
/// 会出现「没画到」的空洞）。</para>
/// </summary>
internal sealed class InvertCircleRenderer
{
    /// <summary>反色区域边缘的软过渡宽度（DIP）：太小有锯齿，太大像蒙了一层毛玻璃。</summary>
    private const double EdgeSoftness = 1.2;

    /// <summary>
    /// 鼠标松开后隔几个基准帧再补取景：导航 / 勾选改完内容后，新的页面要先完成布局与渲染，
    /// 立刻取景可能还是旧画面。2 帧（约 33ms）足够覆盖常见的同步导航。
    /// </summary>
    private const double RefreshDelayFrames = 2;

    /// <summary>同时最多保留几圈（安全阀；正常连点 0.83 秒内最多也就叠几圈）。</summary>
    private const int MaxCircles = 8;

    /// <summary>半径小于这个值就不用画了（还在起点附近）。</summary>
    private const double MinVisibleRadius = 0.5;

    /// <summary>取景源（窗口内容根节点；由覆盖层的 <c>CaptureSource</c> 注入）。</summary>
    private Visual? _source;

    /// <summary>内容根节点下面要补的窗口底色（由覆盖层的 <c>CaptureBackground</c> 注入）。</summary>
    private Brush? _background;

    /// <summary>正在播的反色圈（按触发先后排列：先加的在下层，后加的叠在上面）。</summary>
    private readonly List<Circle> _circles = new(MaxCircles);

    /// <summary>现在有没有反色圈在播（有就会一直订阅渲染循环，直到全部播完）。</summary>
    public bool HasVisual => _circles.Count > 0;

    /// <summary>
    /// 两圈重叠时，重叠处是否**再反一次变回正色**（默认 true，主页设置里的复选框）。
    /// false = 后触发的圆直接盖住先前的（重叠处仍是反色）。
    /// </summary>
    public bool OverlapCancels { get; set; } = true;

    /// <summary>换取景源（窗口内容根节点）：正在播的圈立刻作废，避免拿旧视觉取景。</summary>
    public void SetSource(Visual? source)
    {
        if (ReferenceEquals(_source, source)) return;
        _source = source;
        Clear();
    }

    /// <summary>换窗口底色（跟随主题的 DynamicResource）：下一次取景时自然用上新画刷。</summary>
    public void SetBackground(Brush? background) => _background = background;

    /// <summary>
    /// 在 <paramref name="center"/> 新起一圈反色，并立刻取景一次（**不会**清掉正在播的其它圈）。
    /// <paramref name="radius"/> 是最大半径（DIP，来自主页设置「反色圆环半径」滑块），
    /// <paramref name="overlaySize"/> 只用来把取景区域夹进窗口，
    /// <paramref name="dpiScale"/> 决定取景位图的像素密度（高 DPI 下反色内容才不会糊）。
    /// 没有取景源 / 取景失败时这一圈直接忽略（例如无头自检里 new 出来的覆盖层）。
    /// </summary>
    public void Trigger(Point center, Size overlaySize, double dpiScale, double radius)
    {
        if (_source is null) return;

        var scale = double.IsNaN(dpiScale) || dpiScale <= 0 ? 1.0 : dpiScale;
        var maxRadius = double.IsNaN(radius) ? 1 : Math.Clamp(radius, 1, 2000);

        // 最大外接正方形：整段动画只取这一块，半径变化只影响蒙版。
        // 贴边点击时把它夹进窗口内（取景区域不超出内容根节点，Viewbox 永远落在真实范围内；
        // 圆超出窗口的部分由覆盖层 ClipToBounds 裁掉，观感和网页里「圆被视口裁掉」一致）。
        var desired = new Rect(
            center.X - maxRadius,
            center.Y - maxRadius,
            maxRadius * 2,
            maxRadius * 2);
        var region = Rect.Intersect(desired, new Rect(0, 0, overlaySize.Width, overlaySize.Height));
        if (region.IsEmpty || region.Width < 1 || region.Height < 1) return;

        var circle = new Circle(_source, center, maxRadius, region, scale);
        if (!Capture(circle)) return;   // 取景失败（窗口正在关闭等）：这一圈不显示，其它圈不受影响

        if (_circles.Count >= MaxCircles) _circles.RemoveAt(0);   // 安全阀：丢最旧的一圈
        _circles.Add(circle);
    }

    /// <summary>
    /// 请求补一次取景（鼠标松开时调用）：只作用于**最近触发的那一圈** —— 它才是刚完成的这次点击
    /// （可能触发了导航 / 勾选 / 换主题），更早的圈已经播到一半，换图反而会闪。
    /// 等 <see cref="RefreshDelayFrames"/> 帧再取，给内容变化一点完成布局的时间。
    /// </summary>
    public void RequestRefresh()
    {
        if (_circles.Count == 0) return;
        _circles[^1].RefreshFrames = RefreshDelayFrames;
    }

    /// <summary>按基准帧推进所有圈的时间线：各自老化 / 补取景，播完的逐圈回收。</summary>
    public void Update(double frameSteps)
    {
        if (_circles.Count == 0) return;
        if (double.IsNaN(frameSteps) || frameSteps <= 0) return;

        for (var i = _circles.Count - 1; i >= 0; i--)
        {
            var circle = _circles[i];
            circle.Age += frameSteps;
            if (InvertCircleAnimation.IsFinished(circle.Age))
            {
                _circles.RemoveAt(i);
                continue;
            }

            if (circle.RefreshFrames > 0)
            {
                circle.RefreshFrames -= frameSteps;
                if (circle.RefreshFrames <= 0)
                {
                    circle.RefreshFrames = 0;
                    if (!Capture(circle)) _circles.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>画一帧：旧的在下、新的在上；重叠开关打开时按覆盖次数的奇偶决定取反 / 还原成原色。</summary>
    public void Render(DrawingContext drawingContext)
    {
        if (_circles.Count == 0) return;

        if (!OverlapCancels)
        {
            // 关掉重叠变正色：每圈各自把自己的反色图盖上去，重叠处看到的就是最后画的那一圈
            for (var i = 0; i < _circles.Count; i++)
            {
                var circle = _circles[i];
                var radius = InvertCircleAnimation.RadiusAt(circle.Age, circle.MaxRadius);
                if (!circle.HasFrame || radius < MinVisibleRadius) continue;
                DrawMasked(drawingContext, circle, radius, clip: null, inverted: true);
            }
            return;
        }

        // 奇偶叠加：previousOdd = 前面所有圈里「覆盖次数为奇数」的区域。
        // 第 i 圈：previousOdd 之外的区域再反一次 → 覆盖次数变奇数 → 画反色；
        //          previousOdd 之内的区域再反一次 → 覆盖次数变偶数 → 画回正色。
        Geometry? previousOdd = null;
        for (var i = 0; i < _circles.Count; i++)
        {
            var circle = _circles[i];
            var radius = InvertCircleAnimation.RadiusAt(circle.Age, circle.MaxRadius);
            if (!circle.HasFrame || radius < MinVisibleRadius) continue;

            var disc = new EllipseGeometry(circle.Center, radius, radius);
            if (previousOdd is null)
            {
                DrawMasked(drawingContext, circle, radius, clip: disc, inverted: true);
            }
            else
            {
                var toggle = new CombinedGeometry(GeometryCombineMode.Exclude, disc, previousOdd);
                var restore = new CombinedGeometry(GeometryCombineMode.Intersect, disc, previousOdd);
                DrawMasked(drawingContext, circle, radius, toggle, inverted: true);
                DrawMasked(drawingContext, circle, radius, restore, inverted: false);
            }

            previousOdd = previousOdd is null
                ? disc
                : new CombinedGeometry(GeometryCombineMode.Xor, previousOdd, disc);
        }
    }

    /// <summary>用圆形软边蒙版（+ 可选区域裁剪）把一圈的反色 / 正色位图铺上去。</summary>
    private static void DrawMasked(
        DrawingContext drawingContext, Circle circle, double radius, Geometry? clip, bool inverted)
    {
        // 蒙版 = 圆形区域，边缘留 1.2DIP 软过渡（不然圆边缘是硬锯齿）
        var mask = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = circle.Center,
            GradientOrigin = circle.Center,
            RadiusX = radius,
            RadiusY = radius,
        };
        var solidUntil = Math.Clamp(1.0 - EdgeSoftness / radius, 0.0, 1.0);
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), solidUntil));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));

        if (clip is not null) drawingContext.PushClip(clip);
        drawingContext.PushOpacityMask(mask);
        try
        {
            drawingContext.DrawImage(inverted ? circle.Bitmap : circle.NormalBitmap, circle.Region);
        }
        finally
        {
            drawingContext.Pop();                        // 不透明度蒙版
            if (clip is not null) drawingContext.Pop();  // 区域裁剪
        }
    }

    /// <summary>清空所有圈并释放位图（关特效 / 失活 / 改尺寸 / 换取景源都会走到，必须幂等）。</summary>
    public void Clear() => _circles.Clear();

    /// <summary>
    /// 给一圈取当前画面并逐像素反色。取景失败（窗口正在关闭 / 视觉树已卸载）只记调试输出并返回 false，
    /// 绝不让异常冒到渲染循环里。
    /// </summary>
    private bool Capture(Circle circle)
    {
        try
        {
            // 点击动作可能刚改过界面（例如导航到新页面）：先把布局落地，否则取到的是旧页面 / 半成品
            if (_source is UIElement element && (!element.IsMeasureValid || !element.IsArrangeValid))
                element.UpdateLayout();

            var localRect = new Rect(0, 0, circle.Region.Width, circle.Region.Height);
            using (var drawing = circle.CaptureVisual.RenderOpen())
            {
                // 内容根节点自己是透明的：先补窗口底色，否则反色区域会出现「没画到」的透明空洞
                if (_background is not null) drawing.DrawRectangle(_background, null, localRect);
                drawing.DrawRectangle(circle.SourceBrush, null, localRect);
            }

            circle.Target.Render(circle.CaptureVisual);
            circle.Target.CopyPixels(circle.Pixels, circle.Stride, 0);

            // 先留一份正色（重叠变回正色时要用），再在缓冲里逐像素取反写进反色位图
            var bounds = new Int32Rect(0, 0, circle.Bitmap.PixelWidth, circle.Bitmap.PixelHeight);
            circle.NormalBitmap.WritePixels(bounds, circle.Pixels, circle.Stride, 0);
            InvertPixels(circle.Pixels);
            circle.Bitmap.WritePixels(bounds, circle.Pixels, circle.Stride, 0);
            circle.HasFrame = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InvertCircle] 反色取景失败，放弃这一圈：{ex}");
            circle.HasFrame = false;
            return false;
        }
    }

    /// <summary>
    /// 逐像素反色（Pbgra32 = 预乘 BGRA）：不透明像素直接 255-c；半透明像素先还原成直通色再取反、
    /// 最后重新预乘，保持 alpha 不变（窗口全不透明时只会走最快的分支）。
    /// </summary>
    private static void InvertPixels(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 0) continue;   // 完全透明（窗口外 / 取景区边缘）：反色也看不见，保持 0

            if (alpha == 255)
            {
                pixels[i] = (byte)(255 - pixels[i]);         // B
                pixels[i + 1] = (byte)(255 - pixels[i + 1]); // G
                pixels[i + 2] = (byte)(255 - pixels[i + 2]); // R
                continue;
            }

            var blue = pixels[i] * 255 / alpha;
            var green = pixels[i + 1] * 255 / alpha;
            var red = pixels[i + 2] * 255 / alpha;
            pixels[i] = (byte)((255 - blue) * alpha / 255);
            pixels[i + 1] = (byte)((255 - green) * alpha / 255);
            pixels[i + 2] = (byte)((255 - red) * alpha / 255);
        }
    }

    /// <summary>
    /// 一圈反色动画的**全部私有状态**：触发位置、最大半径、取景区域、取景位图（正色 + 反色两份）、
    /// 自己的时间线进度与补取景倒计时。每圈各自独立，互不影响。
    /// </summary>
    private sealed class Circle
    {
        public Circle(Visual source, Point center, double maxRadius, Rect region, double dpiScale)
        {
            Center = center;
            MaxRadius = maxRadius;
            Region = region;

            var pixelWidth = Math.Max(1, (int)Math.Ceiling(region.Width * dpiScale));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(region.Height * dpiScale));
            var dpi = 96.0 * dpiScale;

            Target = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            Bitmap = new WriteableBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32, null);
            NormalBitmap = new WriteableBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32, null);
            Stride = pixelWidth * 4;
            Pixels = new byte[Stride * pixelHeight];
            CaptureVisual = new DrawingVisual();

            // Viewbox 用「绝对坐标」对齐内容根节点：源区域 = 点击处最大外接正方形，
            // 目标 = 取景位图铺满，所以反色图和真实位置 1:1 对齐。
            SourceBrush = new VisualBrush(source)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = region,
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
        }

        /// <summary>触发位置（覆盖层 DIP 坐标）。</summary>
        public Point Center { get; }

        /// <summary>最大半径（DIP，来自主页设置滑块）。</summary>
        public double MaxRadius { get; }

        /// <summary>取景区域（最大外接正方形，夹进窗口内）。</summary>
        public Rect Region { get; }

        /// <summary>取景目标（每帧不重取，只在触发 / 补取景时用）。</summary>
        public RenderTargetBitmap Target { get; }

        /// <summary>反色后的位图（直接画给用户看的那一张）。</summary>
        public WriteableBitmap Bitmap { get; }

        /// <summary>
        /// 未反色的**正色**位图（同一时刻的取景）。两圈重叠时用来把重叠区域还原成原色
        /// （反色反两次 = 正色）；每圈多占一份位图内存，换来「重叠即消色」的效果。
        /// </summary>
        public WriteableBitmap NormalBitmap { get; }

        /// <summary>取景用 DrawingVisual（复用，避免每次取景 new）。</summary>
        public DrawingVisual CaptureVisual { get; }

        /// <summary>取景画刷（Viewbox 固定 = 这圈的 Region）。</summary>
        public VisualBrush SourceBrush { get; }

        /// <summary>逐像素反色用的临时缓冲（复用）。</summary>
        public byte[] Pixels { get; }

        /// <summary>像素行跨度（字节）。</summary>
        public int Stride { get; }

        /// <summary>已经走过的基准帧数。</summary>
        public double Age { get; set; }

        /// <summary>还要等几帧再补取景（&gt;0 = 已排队；0 = 不补）。</summary>
        public double RefreshFrames { get; set; }

        /// <summary>是否已经取到过画面（取景失败时为 false，不画）。</summary>
        public bool HasFrame { get; set; }
    }
}
