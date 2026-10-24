using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.Core.Particles;

namespace YuLiangToolkit.Core.Controls;

/// <summary>
/// 鼠标粒子覆盖层：铺在窗口最上面，用 <see cref="FrameworkElement.OnRender"/> + <c>DrawingContext</c>
/// 直接画圆点（**每个粒子不是控件**，不往可视化树里塞 Ellipse —— 旧版那样每帧 new 一堆控件，
/// 粒子上限一放开就会卡）。粒子数学全在 <see cref="ParticleEngine"/> 里（纯逻辑、可无头自检），
/// 本控件只负责：接鼠标事件、按刷新率算帧步长、把粒子画成「淡紫柔光 + 近白圆点」。
///
/// <para><b>XAML 用法</b>（放到窗口根 Grid 的最后，注意 Grid.RowSpan 要盖满整个窗口）：</para>
/// <code>
/// &lt;Window xmlns:core="clr-namespace:YuLiangToolkit.Core.Controls;assembly=YuLiangToolkit.Core"&gt;
///   &lt;Grid&gt;
///     ...
///     &lt;core:ParticleOverlay Grid.RowSpan="4" Panel.ZIndex="900"
///                            IsHitTestVisible="False"
///                            IsActive="{Binding ParticlesEnabled}" /&gt;
///   &lt;/Grid&gt;
/// &lt;/Window&gt;
/// </code>
/// <para>只绑定 <see cref="IsActive"/> 就能用；<see cref="ParticleColor"/> / <see cref="GlowColor"/>
/// 有深色主题合适的默认值，一般不用写。控件自己会兜底 <c>IsHitTestVisible = false</c>，但 XAML 里
/// 顺手写上更直观。</para>
///
/// <para><b>行为</b>：鼠标在窗口内 → 每帧从鼠标位置生成粒子（跟随移动）；左键 → 速度 + 数量爆发；
/// 右键 → 数量爆发；鼠标离开窗口 / 窗口失活 / 最小化 → 暂停并清空；<see cref="IsActive"/> = false →
/// 立即清空并停掉渲染循环（<c>CompositionTarget.Rendering</c> 只是订阅，空闲时不会自己跑）。</para>
///
/// <para><b>调试</b>：设环境变量 <c>YLT_PARTICLE_DEMO=1</c> 后，只要 <see cref="IsActive"/> 为 true
/// 就在窗口中部按固定轨迹自动生成粒子（不需要鼠标、也不需要窗口激活），用于 <c>YLT_SHOT</c> 截图验收；
/// 正常情况下这个变量不存在，行为与鼠标模式完全一致。</para>
///
/// <para>所有事件都在 <c>Loaded</c> 时挂到所在 Window、<c>Unloaded</c> / <c>Closed</c> 时成对摘掉，
/// 不残留对窗口/静态 <c>CompositionTarget.Rendering</c> 的引用（后者是静态事件，忘摘就是内存泄漏）。</para>
/// </summary>
public sealed class ParticleOverlay : FrameworkElement
{
    // ================================================================== 依赖属性

    /// <summary>
    /// 是否启用粒子（默认 <c>false</c>）。由主页设置里的开关绑定（例如 <c>{Binding ParticlesEnabled}</c>）。
    /// 设为 false 时立即清空现存粒子、停掉每帧更新并重绘一次（把上一帧擦掉）。
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.None, OnIsActiveChanged));

    /// <summary>
    /// 粒子主色（默认接近白色的淡紫 <c>#F2EEFF</c>，在深色主题上自然）。
    /// 运行时改它只影响之后的绘制，不需要重建任何状态。
    /// </summary>
    public static readonly DependencyProperty ParticleColorProperty = DependencyProperty.Register(
        nameof(ParticleColor), typeof(Color), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata(Color.FromRgb(0xF2, 0xEE, 0xFF),
            FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    /// <summary>
    /// 柔光（外圈）颜色（默认主题淡紫 <c>#C9BDFF</c>，透明度很低，只用来让圆点边缘不那么生硬）。
    /// </summary>
    public static readonly DependencyProperty GlowColorProperty = DependencyProperty.Register(
        nameof(GlowColor), typeof(Color), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata(Color.FromRgb(0xC9, 0xBD, 0xFF),
            FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    /// <summary>是否启用粒子效果。见 <see cref="IsActiveProperty"/>。</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>粒子主色，见 <see cref="ParticleColorProperty"/>。</summary>
    public Color ParticleColor
    {
        get => (Color)GetValue(ParticleColorProperty);
        set => SetValue(ParticleColorProperty, value);
    }

    /// <summary>柔光颜色，见 <see cref="GlowColorProperty"/>。</summary>
    public Color GlowColor
    {
        get => (Color)GetValue(GlowColorProperty);
        set => SetValue(GlowColorProperty, value);
    }

    // ================================================================== 常量 / 字段

    /// <summary>透明度调色板的档数：预生成 N 个冻结画刷，绘制时按 alpha 查表，避免每帧 new 画刷。</summary>
    private const int PaletteLevels = 32;

    /// <summary>柔光外圈的最大不透明度（很低，只是让边缘柔和一点）。</summary>
    private const double GlowMaxAlpha = 0.22;

    /// <summary>柔光半径相对主圆点半径的倍数。</summary>
    private const double GlowRadiusScale = 1.9;

    private readonly ParticleEngine _engine = new();

    /// <summary>开发调试：<c>YLT_PARTICLE_DEMO=1</c> 时不用鼠标、按固定轨迹自动发射（供截图验收）。</summary>
    private readonly bool _demoMode = IsDemoModeEnabled();

    private Brush[] _coreBrushes;
    private Brush[] _glowBrushes;

    private Window? _window;
    private bool _loaded;
    private bool _hooked;          // 窗口事件是否已挂上（成对解绑的开关）
    private bool _renderingHooked; // CompositionTarget.Rendering 是否已挂上
    private bool _pointerInside;   // 鼠标是否在窗口内（离开窗口即暂停，对齐旧版）
    private bool _windowActive;    // 窗口是否激活（失活即暂停）
    private bool _idle = true;     // 上一帧是否"没在生成"（重新开始时把计时归零，避免粒子一上来瞬移）

    private long _lastTick;        // 上一帧的 Stopwatch 时间戳（0 = 重新计时）
    private double _demoSeconds;   // 演示轨迹的累计时间
    private int _demoFrames;       // 演示模式帧计数（隔一段时间来一次小爆发）

    // ================================================================== 构造

    /// <summary>创建覆盖层。放在窗口可视化树里即可，不需要任何初始化调用。</summary>
    public ParticleOverlay()
    {
        // 不挡鼠标（XAML 里也会写，这里兜底）；粒子飞出边界不画到窗口外面
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;

        _coreBrushes = BuildPalette(ParticleColor, 1.0);
        _glowBrushes = BuildPalette(GlowColor, GlowMaxAlpha);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ================================================================== 生命周期 / 事件挂摘

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        _window = Window.GetWindow(this);
        _windowActive = _window?.IsActive == true;
        _pointerInside = false;
        _idle = true;
        _lastTick = 0;

        if (_window is not null) HookWindow(_window);

        _engine.Active = IsActive;
        UpdateRenderingSubscription();

        // 演示模式要主动踢一脚：没有这一帧的 InvalidateVisual，就没有第一次渲染回调，
        // 后面每帧自我延续的渲染循环也就起不来（截图模式下窗口不激活，更要靠它）。
        if (IsActive) InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    /// <summary>解绑所有事件并清空（Unloaded / Window.Closed / 关开关都会走到，必须幂等）。</summary>
    private void Detach()
    {
        _loaded = false;
        StopRendering();

        if (_hooked && _window is not null) UnhookWindow(_window);
        _hooked = false;
        _window = null;

        _engine.Active = false;   // setter 里会 Clear
        _engine.Clear();
        _pointerInside = false;
        _windowActive = false;
        _idle = true;
        _lastTick = 0;

        InvalidateVisual();       // 把上一帧的粒子擦掉（元素已离开可视化树时是安全空操作）
    }

    private void HookWindow(Window window)
    {
        if (_hooked) return;
        _hooked = true;
        window.PreviewMouseMove += OnWindowMouseMove;
        window.PreviewMouseLeftButtonDown += OnWindowMouseLeftButtonDown;
        window.PreviewMouseRightButtonDown += OnWindowMouseRightButtonDown;
        window.MouseEnter += OnWindowMouseEnter;
        window.MouseLeave += OnWindowMouseLeave;
        window.Deactivated += OnWindowDeactivated;
        window.Activated += OnWindowActivated;
        window.StateChanged += OnWindowStateChanged;
        window.Closed += OnWindowClosed;
    }

    private void UnhookWindow(Window window)
    {
        window.PreviewMouseMove -= OnWindowMouseMove;
        window.PreviewMouseLeftButtonDown -= OnWindowMouseLeftButtonDown;
        window.PreviewMouseRightButtonDown -= OnWindowMouseRightButtonDown;
        window.MouseEnter -= OnWindowMouseEnter;
        window.MouseLeave -= OnWindowMouseLeave;
        window.Deactivated -= OnWindowDeactivated;
        window.Activated -= OnWindowActivated;
        window.StateChanged -= OnWindowStateChanged;
        window.Closed -= OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Detach();

    // ================================================================== 开关

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ParticleOverlay)d).ApplyActiveState();

    private void ApplyActiveState()
    {
        var active = IsActive;
        _engine.Active = active;   // 关闭时 setter 内部已 Clear

        if (!active)
        {
            _pointerInside = false;
            _idle = true;
            _lastTick = 0;
            _engine.Clear();
            InvalidateVisual();    // 立刻擦掉上一帧
        }

        UpdateRenderingSubscription();
        if (active) InvalidateVisual();
    }

    /// <summary>
    /// 只有「开关开着 + 已加载 + 窗口没最小化 + （窗口激活 或 演示模式）」时才订阅渲染回调；
    /// 关闭/失活/最小化时**主动退订**，连空转都不留。
    /// </summary>
    private void UpdateRenderingSubscription()
    {
        var shouldRender = _loaded
                           && IsActive
                           && _window is not null
                           && _window.WindowState != WindowState.Minimized
                           && (_demoMode || _windowActive);

        if (shouldRender == _renderingHooked) return;

        if (shouldRender)
        {
            CompositionTarget.Rendering += OnRendering;
            _lastTick = 0;
            _idle = true;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            _lastTick = 0;
        }

        _renderingHooked = shouldRender;
    }

    private void StopRendering()
    {
        if (!_renderingHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
        _lastTick = 0;
    }

    // ================================================================== 鼠标事件

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_loaded || !IsActive || _demoMode) return;

        var position = e.GetPosition(this);
        _pointerInside = true;
        _engine.SetEmitter(position.X, position.Y);
        Wake();
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_loaded || !IsActive || _demoMode) return;

        var position = e.GetPosition(this);
        _pointerInside = true;
        _engine.SetEmitter(position.X, position.Y);
        Wake();
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_loaded) return;

        // 与旧版一致：鼠标离开窗口就清空，回来重新开始（不是"暂停在原地"）
        _pointerInside = false;
        _engine.Clear();
        _idle = true;
        _lastTick = 0;
        InvalidateVisual();
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_loaded || !IsActive || _demoMode) return;

        var position = e.GetPosition(this);
        _pointerInside = true;
        _engine.SetEmitter(position.X, position.Y);
        _engine.BoostSpeedAndCount();   // 左键：速度 + 数量都拉满
        Wake();
    }

    private void OnWindowMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_loaded || !IsActive || _demoMode) return;

        var position = e.GetPosition(this);
        _pointerInside = true;
        _engine.SetEmitter(position.X, position.Y);
        _engine.BoostCount();           // 右键：只加数量
        Wake();
    }

    /// <summary>鼠标进来/点击之后把渲染循环"叫醒"：空闲时事件不会自己来，靠一次重绘引起下一帧。</summary>
    private void Wake()
    {
        if (!_loaded || !IsActive) return;
        UpdateRenderingSubscription();
        if (_idle) _lastTick = 0;   // 别把空闲那段时间算成帧步长，否则粒子一上来就瞬移
        InvalidateVisual();
    }

    // ================================================================== 窗口状态

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _windowActive = true;
        UpdateRenderingSubscription();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _windowActive = false;
        _pointerInside = false;
        _idle = true;
        _lastTick = 0;
        _engine.Clear();          // 失活时清空（对齐旧版离开窗口就清空；回来重新开始）
        InvalidateVisual();
        UpdateRenderingSubscription();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window?.WindowState == WindowState.Minimized)
        {
            _pointerInside = false;
            _idle = true;
            _lastTick = 0;
            _engine.Clear();
            InvalidateVisual();
        }

        UpdateRenderingSubscription();
    }

    // ================================================================== 每帧

    private void OnRendering(object? sender, EventArgs e)
    {
        // 渲染回调里抛异常没人接（会直接崩程序）：兜住并只在调试输出里记一笔。
        try
        {
            AdvanceFrame();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ParticleOverlay] 粒子帧更新失败：{ex}");
        }
    }

    private void AdvanceFrame()
    {
        if (!_loaded || !IsActive) return;

        var window = _window;
        if (window is null) return;
        if (window.WindowState == WindowState.Minimized) return;

        var generating = _demoMode || (window.IsActive && _pointerInside);
        if (!generating)
        {
            // 没在生成：把计时归零，等下一次 Wake()，这样不会把空闲时间算进第一帧
            _idle = true;
            _lastTick = 0;
            return;
        }

        // 用真实经过时间换算成「60fps 基准帧」：高刷屏步长小、低刷屏步长大，速度与寿命都不变
        var now = Stopwatch.GetTimestamp();
        var frameSteps = 1.0;
        if (_lastTick != 0 && now > _lastTick)
        {
            var seconds = (now - _lastTick) / (double)Stopwatch.Frequency;
            frameSteps = seconds * ParticleEngine.ReferenceFps;
            if (double.IsNaN(frameSteps) || double.IsInfinity(frameSteps)) frameSteps = 1.0;
        }
        _lastTick = now;
        frameSteps = Math.Clamp(frameSteps, 0.05, ParticleEngine.MaxFrameStep);

        if (_demoMode) UpdateDemoEmitter(frameSteps);

        _engine.Update(frameSteps);
        _idle = false;
        InvalidateVisual();   // 主动重绘 → 下一帧渲染回调继续来（自维持循环）
    }

    /// <summary>演示模式：发射点沿窗口中部一个李萨如轨迹跑，隔一阵来一次小爆发 —— 无鼠标也能截图看效果。</summary>
    private void UpdateDemoEmitter(double frameSteps)
    {
        _demoSeconds += frameSteps / ParticleEngine.ReferenceFps;

        var width = ActualWidth > 1 ? ActualWidth : 960;
        var height = ActualHeight > 1 ? ActualHeight : 640;
        var centerX = width / 2;
        var centerY = height / 2;
        var radiusX = Math.Min(width * 0.24, 300);
        var radiusY = Math.Min(height * 0.22, 220);

        // 轨迹速度按"鼠标挥动"的量级来（约 5px/帧），截图里是一条连续的弧，不是一颗颗孤立的点
        var t = _demoSeconds;
        _engine.SetEmitter(
            centerX + Math.Cos(t * 1.1) * radiusX,
            centerY + Math.Sin(t * 1.5) * radiusY);

        _demoFrames++;
        if (_demoFrames % 12 == 0) _engine.BoostCount();   // 周期性来一小团，截图里看得出“爆一下”
    }

    // ================================================================== 绘制

    /// <summary>把当前所有粒子画成圆点：外层淡紫柔光 + 内层近白实心，透明度/大小随寿命衰减。</summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var particles = _engine.Particles;
        if (particles.Count == 0) return;

        for (var i = 0; i < particles.Count; i++)
        {
            var particle = particles[i];
            var radius = particle.Size * 0.5;
            if (radius <= 0.15 || particle.Alpha <= 0.01) continue;   // 快消失的粒子不值得画

            var center = new Point(particle.X, particle.Y);
            var brushIndex = AlphaBucket(particle.Alpha);

            // 柔光只在圆点还够大时画，省掉大量"看不见的小点"的绘制开销
            if (radius >= 1.2)
            {
                var glowRadius = radius * GlowRadiusScale + 0.5;
                drawingContext.DrawEllipse(_glowBrushes[brushIndex], null, center, glowRadius, glowRadius);
            }

            drawingContext.DrawEllipse(_coreBrushes[brushIndex], null, center, radius, radius);
        }
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var overlay = (ParticleOverlay)d;
        overlay.RebuildPalettes();
        overlay.InvalidateVisual();
    }

    private void RebuildPalettes()
    {
        _coreBrushes = BuildPalette(ParticleColor, 1.0);
        _glowBrushes = BuildPalette(GlowColor, GlowMaxAlpha);
    }

    /// <summary>
    /// 预生成一档一档的半透明画刷（全部冻结）：绘制时按 alpha 查表，**每帧零分配**。
    /// 旧版每个粒子每帧 new 一个 SolidColorBrush，粒子上限一放开就是 GC 灾难。
    /// </summary>
    private static Brush[] BuildPalette(Color color, double maxAlpha)
    {
        var brushes = new Brush[PaletteLevels];
        for (var i = 0; i < PaletteLevels; i++)
        {
            var alpha = maxAlpha * (i + 1) / PaletteLevels;
            var brush = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(Math.Clamp(alpha, 0.0, 1.0) * 255), color.R, color.G, color.B));
            brush.Freeze();   // 冻结后可以跨线程复用、渲染更快
            brushes[i] = brush;
        }
        return brushes;
    }

    private static int AlphaBucket(double alpha)
    {
        var index = (int)Math.Round(Math.Clamp(alpha, 0.0, 1.0) * (PaletteLevels - 1));
        if (index < 0) return 0;
        if (index >= PaletteLevels) return PaletteLevels - 1;
        return index;
    }

    private static bool IsDemoModeEnabled()
    {
        var value = Environment.GetEnvironmentVariable("YLT_PARTICLE_DEMO");
        return string.Equals(value?.Trim(), "1", StringComparison.Ordinal);
    }
}
