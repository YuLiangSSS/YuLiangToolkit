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
///   <item>窗口尺寸变化时直接结束这一圈（缩放后旧取景的比例已经不对了）。</item>
/// </list>
/// 这样「点导航卡片 → 反色的是新页面」这类常见路径不会露出旧画面的反色残影，代价只有点击时约一帧的取景。</para>
///
/// <para>窗口底色由覆盖层的 <c>CaptureBackground</c> 补上（内容根节点自己是透明的，不补的话反色区域
/// 会出现「没画到」的空洞）。同时只保留**一圈**：连点会丢掉上一圈、从新的点击位置重新开始。</para>
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

    /// <summary>取景源（窗口内容根节点；由覆盖层的 <c>CaptureSource</c> 注入）。</summary>
    private Visual? _source;

    /// <summary>内容根节点下面要补的窗口底色（由覆盖层的 <c>CaptureBackground</c> 注入）。</summary>
    private Brush? _background;

    private Point _center;
    private double _maxRadius;
    private double _age;
    private double _dpiScale = 1;

    /// <summary>反色圆的最大外接正方形（覆盖层 DIP 坐标）。</summary>
    private Rect _region;

    private RenderTargetBitmap? _target;
    private WriteableBitmap? _bitmap;
    private DrawingVisual? _captureVisual;
    private VisualBrush? _sourceBrush;
    private byte[]? _pixels;
    private int _stride;
    private bool _hasFrame;

    /// <summary>还要等几帧再补取景（&gt;0 = 已排队；0 = 不补）。</summary>
    private double _refreshFrames;

    /// <summary>现在有没有一圈反色在播（有就会一直订阅渲染循环，直到播完）。</summary>
    public bool HasVisual => _bitmap is not null && !InvertCircleAnimation.IsFinished(_age);

    /// <summary>换取景源（窗口内容根节点）：正在播的一圈立刻作废，避免拿旧视觉取景。</summary>
    public void SetSource(Visual? source)
    {
        if (ReferenceEquals(_source, source)) return;
        _source = source;
        Clear();
    }

    /// <summary>换窗口底色（跟随主题的 DynamicResource）：下一帧取景时自然用上新画刷。</summary>
    public void SetBackground(Brush? background) => _background = background;

    /// <summary>
    /// 在 <paramref name="center"/> 触发一圈反色，并立刻取景一次。
    /// <paramref name="radius"/> 是最大半径（DIP，来自主页设置「反色圆环半径」滑块），
    /// <paramref name="overlaySize"/> 只用来把取景区域夹进窗口，
    /// <paramref name="dpiScale"/> 决定取景位图的像素密度（高 DPI 下反色内容才不会糊）。
    /// 没有取景源时直接忽略（例如无头自检里 new 出来的覆盖层）。
    /// </summary>
    public void Trigger(Point center, Size overlaySize, double dpiScale, double radius)
    {
        Clear();
        if (_source is null) return;

        _center = center;
        _maxRadius = double.IsNaN(radius) ? 1 : Math.Clamp(radius, 1, 2000);
        _age = 0;
        _dpiScale = double.IsNaN(dpiScale) || dpiScale <= 0 ? 1.0 : dpiScale;

        // 最大外接正方形：整段动画只取这一块，半径变化只影响蒙版。
        // 贴边点击时把它夹进窗口内（取景区域不超出内容根节点，Viewbox 永远落在真实范围内；
        // 圆超出窗口的部分由覆盖层 ClipToBounds 裁掉，观感和网页里「圆被视口裁掉」一致）。
        var desired = new Rect(
            center.X - _maxRadius,
            center.Y - _maxRadius,
            _maxRadius * 2,
            _maxRadius * 2);
        _region = Rect.Intersect(desired, new Rect(0, 0, overlaySize.Width, overlaySize.Height));
        if (_region.IsEmpty || _region.Width < 1 || _region.Height < 1)
        {
            Clear();
            return;
        }

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(_region.Width * _dpiScale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(_region.Height * _dpiScale));
        var dpi = 96.0 * _dpiScale;

        _target = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        _bitmap = new WriteableBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32, null);
        _stride = pixelWidth * 4;
        _pixels = new byte[_stride * pixelHeight];
        _captureVisual = new DrawingVisual();

        // Viewbox 用「绝对坐标」对齐内容根节点：源区域 = 点击处最大外接正方形，
        // 目标 = 取景位图铺满，所以反色图和真实位置 1:1 对齐。
        _sourceBrush = new VisualBrush(_source)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = _region,
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };

        CaptureOnce();
    }

    /// <summary>
    /// 请求补一次取景（鼠标松开时调用）：等 <see cref="RefreshDelayFrames"/> 帧再取，
    /// 给「点击触发的内容变化（导航 / 勾选 / 换主题）」一点完成布局的时间。
    /// 这一圈已经播完 / 没在播时什么都不做。
    /// </summary>
    public void RequestRefresh()
    {
        if (!HasVisual) return;
        _refreshFrames = RefreshDelayFrames;
    }

    /// <summary>按基准帧推进时间线：播完清空；排队中的补取景到点执行。</summary>
    public void Update(double frameSteps)
    {
        if (_bitmap is null) return;
        if (double.IsNaN(frameSteps) || frameSteps <= 0) return;

        _age += frameSteps;
        if (InvertCircleAnimation.IsFinished(_age))
        {
            Clear();
            return;
        }

        if (_refreshFrames > 0)
        {
            _refreshFrames -= frameSteps;
            if (_refreshFrames <= 0)
            {
                _refreshFrames = 0;
                CaptureOnce();
            }
        }
    }

    /// <summary>画一帧：按当前半径用径向渐变不透明度蒙版，把反色位图铺到点击处。</summary>
    public void Render(DrawingContext drawingContext)
    {
        if (!_hasFrame || _bitmap is null) return;

        var radius = InvertCircleAnimation.RadiusAt(_age, _maxRadius);
        if (radius < 0.5) return;

        // 蒙版 = 圆形区域，边缘留 1.2DIP 软过渡（不然圆边缘是硬锯齿）
        var mask = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = _center,
            GradientOrigin = _center,
            RadiusX = radius,
            RadiusY = radius,
        };
        var solidUntil = Math.Clamp(1.0 - EdgeSoftness / radius, 0.0, 1.0);
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), solidUntil));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));

        drawingContext.PushOpacityMask(mask);
        try
        {
            drawingContext.DrawImage(_bitmap, _region);
        }
        finally
        {
            drawingContext.Pop();
        }
    }

    /// <summary>清空当前一圈并释放位图（关特效 / 播完 / 失活 / 改尺寸 / 换取景源都会走到，必须幂等）。</summary>
    public void Clear()
    {
        _bitmap = null;
        _target = null;
        _captureVisual = null;
        _sourceBrush = null;
        _pixels = null;
        _stride = 0;
        _hasFrame = false;
        _refreshFrames = 0;
        _age = 0;
        _maxRadius = 0;
    }

    /// <summary>
    /// 取当前画面并逐像素反色。取景失败（窗口正在关闭 / 视觉树已卸载）只记调试输出并放弃这一圈，
    /// 绝不让异常冒到渲染循环里。
    /// </summary>
    private void CaptureOnce()
    {
        if (_target is null || _bitmap is null || _captureVisual is null || _sourceBrush is null || _pixels is null)
            return;

        try
        {
            // 点击动作可能刚改过界面（例如导航到新页面）：先把布局落地，否则取到的是旧页面 / 半成品
            if (_source is UIElement element && (!element.IsMeasureValid || !element.IsArrangeValid))
                element.UpdateLayout();

            var localRect = new Rect(0, 0, _region.Width, _region.Height);
            using (var drawing = _captureVisual.RenderOpen())
            {
                // 内容根节点自己是透明的：先补窗口底色，否则反色区域会出现「没画到」的透明空洞
                if (_background is not null) drawing.DrawRectangle(_background, null, localRect);
                drawing.DrawRectangle(_sourceBrush, null, localRect);
            }

            _target.Render(_captureVisual);
            _target.CopyPixels(_pixels, _stride, 0);
            InvertPixels(_pixels);
            _bitmap.WritePixels(
                new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight), _pixels, _stride, 0);
            _hasFrame = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InvertCircle] 反色取景失败，本圈放弃：{ex}");
            Clear();
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
}
