using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.Core.Controls.CursorEffects;
using YuLiangToolkit.Core.Effects;
using CursorEffectCatalog = YuLiangToolkit.Core.Effects.CursorEffects;
using YuLiangToolkit.Core.Particles;

namespace YuLiangToolkit.Core.Controls;

/// <summary>
/// 光标特效覆盖层（v0.26.0 由原来的「粒子覆盖层」扩展而来）：铺在窗口最上面，
/// 用 <see cref="FrameworkElement.OnRender"/> + <c>DrawingContext</c> 直接画，
/// 不往可视化树里塞任何控件。
///
/// <para><b>特效由清单驱动、可以任意组合</b>（见 <see cref="CursorEffects"/>）：</para>
/// <list type="bullet">
///   <item><b>自动效果</b>（<see cref="AutoEffects"/>，多选）：<c>particle-trail</c> 粒子拖尾（原有效果）、
///   <c>cursor-glow</c> 鼠标散光（鼠标位置一圈微弱圆光）；</item>
///   <item><b>点击效果</b>（<see cref="ClickEffects"/>，可多选）：<c>particle-burst</c> 粒子爆炸、
///   <c>dispersion</c> 散光扩散（Glassium 风格彩色色散环）、<c>glow-flash</c> 光晕闪烁。</item>
/// </list>
/// <para>两套特效**完全独立**：自动效果全部不勾也能有点击爆发；点击效果可以同时勾好几个。
/// 新增一种特效的步骤见 <see cref="CursorEffects"/> 的注释（加清单 + 加渲染器 + 加对应字段/分支）。</para>
///
/// <para><b>XAML 用法</b>（放在窗口根 Grid 的最后，Grid.RowSpan 盖满整个窗口）：</para>
/// <code>
/// &lt;core:ParticleOverlay Grid.RowSpan="4" Panel.ZIndex="900" IsHitTestVisible="False"
///                        IsActive="True"
///                        AutoEffects="{Binding AutoEffectIds}"
///                        ClickEffects="{Binding ClickEffectIds}"
///                        UseCustomColors="{Binding ParticleCustomColorsEnabled}" ... /&gt;
/// </code>
///
/// <para><b>性能</b>：只在「有自动拖尾在生成」或「有爆炸/散光/闪光在播」时才订阅
/// <c>CompositionTarget.Rendering</c>（没有特效应画时立刻退订，空闲零 CPU）；
/// 鼠标散光只在鼠标移动时重绘一次，不常驻 60fps。所有画刷 / 画笔预生成并冻结，绘制路径零 new。</para>
///
/// <para><b>调试</b>：<c>YLT_PARTICLE_DEMO=1</c>（或 <c>YLT_EFFECT_DEMO=1</c>）进入演示模式 ——
/// 不需要鼠标、按固定轨迹自动产生拖尾并周期性触发点击特效，供 <c>YLT_SHOT</c> 截图验收；
/// <c>YLT_PARTICLE_TRACE=&lt;文件&gt;</c> 把鼠标坐标 / 每 20 帧的发射点与存活数写进文件。</para>
/// </summary>
public sealed class ParticleOverlay : FrameworkElement
{
    // ================================================================== 依赖属性

    /// <summary>
    /// 总开关（默认 <c>false</c>）。为 false 时所有特效立即清空并停止渲染循环。
    /// 具体开哪些特效由 <see cref="AutoEffects"/> / <see cref="ClickEffects"/> 决定。
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.None, OnIsActiveChanged));

    /// <summary>
    /// 自动效果的 Id 列表（逗号分隔，见 <see cref="CursorEffects"/>；空串 = 没有自动效果）。
    /// 例如 <c>"particle-trail,cursor-glow"</c>。未知 Id 会被忽略。
    /// </summary>
    public static readonly DependencyProperty AutoEffectsProperty = DependencyProperty.Register(
        nameof(AutoEffects), typeof(string), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.None, OnEffectsChanged));

    /// <summary>
    /// 点击效果的 Id 列表（逗号分隔，可多选；空串 = 点击没有任何特效）。
    /// 例如 <c>"particle-burst,dispersion,glow-flash"</c>。未知 Id 会被忽略。
    /// </summary>
    public static readonly DependencyProperty ClickEffectsProperty = DependencyProperty.Register(
        nameof(ClickEffects), typeof(string), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.None, OnEffectsChanged));

    /// <summary>粒子主色（默认接近白色的淡紫 <c>#F2EEFF</c>，在深色主题上自然）。</summary>
    public static readonly DependencyProperty ParticleColorProperty = DependencyProperty.Register(
        nameof(ParticleColor), typeof(Color), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata(Color.FromRgb(0xF2, 0xEE, 0xFF),
            FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    /// <summary>柔光（外圈）颜色（默认主题淡紫 <c>#C9BDFF</c>，透明度很低，让圆点边缘不那么生硬）。</summary>
    public static readonly DependencyProperty GlowColorProperty = DependencyProperty.Register(
        nameof(GlowColor), typeof(Color), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata(Color.FromRgb(0xC9, 0xBD, 0xFF),
            FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    // ---------- 自定义粒子颜色（v0.22.0；对所有「粒子类」特效生效：拖尾 + 爆炸） ----------
    //
    // 语义（主页设置「粒子颜色」卡片直接绑这几个 DP）：
    //   UseCustomColors=false          → 用 ParticleColor / GlowColor（原来的淡紫白，行为不变）；
    //   UseCustomColors=true, 只勾一种 → 粒子固定是那种颜色（柔光用同色）；
    //   UseCustomColors=true, 勾两种   → 每颗粒子生成时的随机 ColorMix 在两种颜色之间线性插值；
    //   两种都没勾                     → 退回默认淡紫白（界面会提示）。

    /// <summary>是否启用自定义特效颜色（默认 false = 原来的淡紫白）。</summary>
    public static readonly DependencyProperty UseCustomColorsProperty = DependencyProperty.Register(
        nameof(UseCustomColors), typeof(bool), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    /// <summary>自定义颜色 1（默认 <c>#F2EEFF</c>）。</summary>
    public static readonly DependencyProperty Color1Property = DependencyProperty.Register(
        nameof(Color1), typeof(Color), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata(Color.FromRgb(0xF2, 0xEE, 0xFF),
            FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    /// <summary>自定义颜色 2（默认 <c>#8FB4FF</c>）。</summary>
    public static readonly DependencyProperty Color2Property = DependencyProperty.Register(
        nameof(Color2), typeof(Color), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata(Color.FromRgb(0x8F, 0xB4, 0xFF),
            FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    /// <summary>颜色 1 是否参与取色（默认 true）。</summary>
    public static readonly DependencyProperty UseColor1Property = DependencyProperty.Register(
        nameof(UseColor1), typeof(bool), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    /// <summary>颜色 2 是否参与取色（默认 true；只勾一种时是单色）。</summary>
    public static readonly DependencyProperty UseColor2Property = DependencyProperty.Register(
        nameof(UseColor2), typeof(bool), typeof(ParticleOverlay),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    /// <summary>总开关，见 <see cref="IsActiveProperty"/>。</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>自动效果 Id 列表（逗号分隔），见 <see cref="AutoEffectsProperty"/>。</summary>
    public string AutoEffects
    {
        get => (string)GetValue(AutoEffectsProperty);
        set => SetValue(AutoEffectsProperty, value);
    }

    /// <summary>点击效果 Id 列表（逗号分隔），见 <see cref="ClickEffectsProperty"/>。</summary>
    public string ClickEffects
    {
        get => (string)GetValue(ClickEffectsProperty);
        set => SetValue(ClickEffectsProperty, value);
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

    /// <summary>是否启用自定义特效颜色，见 <see cref="UseCustomColorsProperty"/>。</summary>
    public bool UseCustomColors
    {
        get => (bool)GetValue(UseCustomColorsProperty);
        set => SetValue(UseCustomColorsProperty, value);
    }

    /// <summary>自定义颜色 1，见 <see cref="Color1Property"/>。</summary>
    public Color Color1
    {
        get => (Color)GetValue(Color1Property);
        set => SetValue(Color1Property, value);
    }

    /// <summary>自定义颜色 2，见 <see cref="Color2Property"/>。</summary>
    public Color Color2
    {
        get => (Color)GetValue(Color2Property);
        set => SetValue(Color2Property, value);
    }

    /// <summary>颜色 1 是否参与取色，见 <see cref="UseColor1Property"/>。</summary>
    public bool UseColor1
    {
        get => (bool)GetValue(UseColor1Property);
        set => SetValue(UseColor1Property, value);
    }

    /// <summary>颜色 2 是否参与取色，见 <see cref="UseColor2Property"/>。</summary>
    public bool UseColor2
    {
        get => (bool)GetValue(UseColor2Property);
        set => SetValue(UseColor2Property, value);
    }

    // ================================================================== 常量 / 字段

    /// <summary>透明度调色板的档数：预生成 N 个冻结画刷，绘制时按 alpha 查表，避免每帧 new 画刷。</summary>
    private const int PaletteLevels = 32;

    /// <summary>两种自定义颜色之间切几档（12 档肉眼看就是连续渐变；档数越多画刷越多）。</summary>
    private const int ColorBuckets = 12;

    /// <summary>柔光外圈的最大不透明度（很低，只是让边缘柔和一点）。</summary>
    private const double GlowMaxAlpha = 0.22;

    /// <summary>柔光半径相对主圆点半径的倍数。</summary>
    private const double GlowRadiusScale = 1.9;

    /// <summary>自动拖尾引擎（纯逻辑；自动效果关掉时完全不工作）。</summary>
    private readonly ParticleEngine _trailEngine = new();

    /// <summary>点击爆炸引擎（纯逻辑；和自动拖尾互相独立）。</summary>
    private readonly ParticleBurstEngine _burstEngine = new();

    private readonly CursorGlowRenderer _cursorGlow = new();
    private readonly DispersionRippleRenderer _dispersion = new();
    private readonly GlowFlashRenderer _flash = new();

    /// <summary>开发调试：<c>YLT_PARTICLE_DEMO=1</c> / <c>YLT_EFFECT_DEMO=1</c> 时不用鼠标、自动演示（供截图）。</summary>
    private readonly bool _demoMode = IsDemoModeEnabled();

    /// <summary>当前勾选了哪些自动效果（来自 <see cref="AutoEffects"/>）。</summary>
    private bool _trailEnabled;
    private bool _glowEnabled;

    /// <summary>当前勾选了哪些点击效果（来自 <see cref="ClickEffects"/>）。</summary>
    private bool _burstEnabled;
    private bool _dispersionEnabled;
    private bool _flashEnabled;

    /// <summary>颜色档数 × 透明度档数的冻结画刷表；第一维是颜色档（1 = 单色 / 默认）。</summary>
    private Brush[,] _coreBrushes = null!;
    private Brush[,] _glowBrushes = null!;

    /// <summary>当前颜色档数（= <see cref="_coreBrushes"/> 第一维长度，缓存下来避免每帧查）。</summary>
    private int _colorCount = 1;

    private Window? _window;
    private bool _loaded;
    private bool _hooked;          // 窗口事件是否已挂上（成对解绑的开关）
    private bool _renderingHooked; // CompositionTarget.Rendering 是否已挂上
    private bool _pointerInside;   // 鼠标是否在窗口内（离开窗口即清空）
    private bool _windowActive;    // 窗口是否激活（失活即暂停）
    private bool _idle = true;     // 上一帧是否"没在生成"（重新开始时把计时归零，避免粒子一上来瞬移）

    private Point _pointerPosition;   // 鼠标在覆盖层里的最新位置（散光 / 点击特效用）
    private double _effectSeconds;    // 累计秒数（散光呼吸脉动用）

    private long _lastTick;        // 上一帧的 Stopwatch 时间戳（0 = 重新计时）
    private double _demoSeconds;   // 演示轨迹的累计时间
    private int _demoFrames;       // 演示模式帧计数（周期性触发展示效果）

    // ================================================================== 构造

    /// <summary>创建覆盖层。放在窗口可视化树里即可，不需要任何初始化调用。</summary>
    public ParticleOverlay()
    {
        // 不挡鼠标（XAML 里也会写，这里兜底）；特效飞出边界不画到窗口外面
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;

        RebuildPalettes();
        ApplyEffectSelection();

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

        _trailEngine.Active = IsActive && _trailEnabled;
        UpdateRenderingSubscription();

        // 演示模式要主动踢一脚：没有这一帧的 InvalidateVisual，就没有第一次渲染回调，
        // 后面每帧自我延续的渲染循环也就起不来（截图模式下窗口不激活，更要靠它）。
        if (IsActive && (_trailEnabled || _demoMode)) InvalidateVisual();
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

        ResetAllEffects();
        _pointerInside = false;
        _windowActive = false;
        _idle = true;
        _lastTick = 0;

        InvalidateVisual();       // 把上一帧的特效擦掉（元素已离开可视化树时是安全空操作）
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

    // ================================================================== 开关 / 特效选择

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ParticleOverlay)d).ApplyActiveState();

    private static void OnEffectsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ParticleOverlay)d).ApplyEffectSelection();

    /// <summary>总开关变化：关掉时清空一切并停渲染；打开时按选择恢复自动拖尾并订阅渲染。</summary>
    private void ApplyActiveState()
    {
        var active = IsActive;
        _trailEngine.Active = active && _trailEnabled;

        if (!active)
        {
            ResetTransientEffects();
            _pointerInside = false;
            _idle = true;
            _lastTick = 0;
        }

        UpdateRenderingSubscription();
        if (active) InvalidateVisual();
    }

    /// <summary>
    /// 重新解析 <see cref="AutoEffects"/> / <see cref="ClickEffects"/>：未知 Id 忽略，
    /// 被关掉的特效立刻清空；拖尾引擎跟着开关。新增渲染分支加在这里。
    /// </summary>
    private void ApplyEffectSelection()
    {
        var auto = CursorEffectCatalog.Normalize(
            CursorEffectCatalog.Split(AutoEffects), CursorEffectTrigger.Auto, Array.Empty<string>());
        var click = CursorEffectCatalog.Normalize(
            CursorEffectCatalog.Split(ClickEffects), CursorEffectTrigger.Click, Array.Empty<string>());

        _trailEnabled = auto.Contains(CursorEffectCatalog.ParticleTrail, StringComparer.OrdinalIgnoreCase);
        _glowEnabled = auto.Contains(CursorEffectCatalog.CursorGlow, StringComparer.OrdinalIgnoreCase);
        _burstEnabled = click.Contains(CursorEffectCatalog.ParticleBurst, StringComparer.OrdinalIgnoreCase);
        _dispersionEnabled = click.Contains(CursorEffectCatalog.Dispersion, StringComparer.OrdinalIgnoreCase);
        _flashEnabled = click.Contains(CursorEffectCatalog.GlowFlash, StringComparer.OrdinalIgnoreCase);

        _trailEngine.Active = _loaded ? IsActive && _trailEnabled : _trailEnabled;
        if (!_trailEnabled) _trailEngine.Clear();
        if (!_burstEnabled) _burstEngine.Clear();
        if (!_dispersionEnabled) _dispersion.Clear();
        if (!_flashEnabled) _flash.Clear();
        _cursorGlow.Visible = false;   // 每帧渲染时按开关和鼠标状态再置

        UpdateRenderingSubscription();
        InvalidateVisual();
    }

    /// <summary>清空所有一次性特效（爆炸 / 散光 / 闪光）。</summary>
    private void ResetTransientEffects()
    {
        _burstEngine.Clear();
        _dispersion.Clear();
        _flash.Clear();
    }

    /// <summary>清空所有特效（拖尾 + 一次性 + 散光显隐）。</summary>
    private void ResetAllEffects()
    {
        _trailEngine.Active = false;   // setter 里会 Clear
        _trailEngine.Clear();
        ResetTransientEffects();
        _cursorGlow.Visible = false;
    }

    // ================================================================== 渲染订阅

    /// <summary>自动拖尾还在连续生成吗（只有它需要 60fps 循环）。</summary>
    private bool HasContinuousWork()
        => _trailEnabled && IsActive && (_demoMode || _pointerInside);

    /// <summary>有一次性特效（爆炸 / 散光 / 闪光）在播吗。</summary>
    private bool HasTransientWork()
        => (_burstEnabled && _burstEngine.HasParticles)
           || (_dispersionEnabled && _dispersion.HasVisual)
           || (_flashEnabled && _flash.HasVisual);

    /// <summary>
    /// 只有「总开关开着 + 已加载 + 窗口没最小化 + （窗口激活 或 演示模式）+ 确实有东西要画」时
    /// 才订阅渲染回调；其余情况**主动退订**，连空转都不留（鼠标散光只在移动时重绘一次）。
    /// </summary>
    private void UpdateRenderingSubscription()
    {
        var shouldRender = _loaded
                           && IsActive
                           && _window is not null
                           && _window.WindowState != WindowState.Minimized
                           && (_demoMode || _windowActive)
                           && (HasContinuousWork() || HasTransientWork());

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

    /// <summary>
    /// 取鼠标在覆盖层里的真实位置。
    ///
    /// <para>⚠ <b>不能只用 <see cref="MouseEventArgs.GetPosition"/>（v0.17.0 修）</b>：点标题栏拖动窗口时
    /// WPF 会额外派发一个坐标为 <c>(0,0)</c> 的“假”鼠标事件（实测 <c>DragMove()</c> 期间
    /// <c>e.GetPosition</c> 和 <c>Mouse.GetPosition</c> 都返回 0,0），发射点被甩到窗口左上角 ——
    /// 现象就是「小窗里点一下，粒子同时从左上角冒出来、往右下飞」；窗口最大化时标题栏不触发拖拽，
    /// 所以只有小窗有这毛病。这里改用 Win32 的 <c>GetCursorPos</c> 取真实屏幕坐标再换算回来。</para>
    /// </summary>
    private Point GetPointerPosition(MouseEventArgs e)
    {
        if (TryGetRealCursorPosition(out var point)) return point;
        return e.GetPosition(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private bool TryGetRealCursorPosition(out Point point)
    {
        point = default;
        if (PresentationSource.FromVisual(this) is null) return false;
        if (!GetCursorPos(out var screen)) return false;
        try
        {
            point = PointFromScreen(new Point(screen.X, screen.Y));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_loaded || !IsActive || _demoMode) return;

        var position = GetPointerPosition(e);
        Trace($"move pos={position.X:0.#},{position.Y:0.#} event={e.GetPosition(this).X:0.#},{e.GetPosition(this).Y:0.#}");
        TrackPointer(position);
        Wake();
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_loaded || !IsActive || _demoMode) return;

        var position = GetPointerPosition(e);
        Trace($"enter pos={position.X:0.#},{position.Y:0.#} event={e.GetPosition(this).X:0.#},{e.GetPosition(this).Y:0.#}");
        TrackPointer(position);
        Wake();
    }

    /// <summary>记住最新鼠标位置：拖尾引擎发射点 + 散光 / 点击特效的落点。</summary>
    private void TrackPointer(Point position)
    {
        _pointerInside = true;
        _pointerPosition = position;
        if (_trailEnabled) _trailEngine.SetEmitter(position.X, position.Y);
    }

    // ---------- 调试（YLT_PARTICLE_TRACE=文件） ----------
    private static readonly string? TracePath = Environment.GetEnvironmentVariable("YLT_PARTICLE_TRACE");
    private int _traceFrame;
    private static void Trace(string line)
    {
        if (string.IsNullOrWhiteSpace(TracePath)) return;
        try { File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch { /* 忽略 */ }
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_loaded) return;

        // 与早期版本一致：鼠标离开窗口就清空，回来重新开始（不是"暂停在原地"）
        _pointerInside = false;
        ResetAllEffects();
        _trailEngine.Clear();
        _idle = true;
        _lastTick = 0;
        UpdateRenderingSubscription();
        InvalidateVisual();
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => HandleMouseClick(e, isRight: false);

    private void OnWindowMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => HandleMouseClick(e, isRight: true);

    /// <summary>鼠标按下：先更新发射点，再按「自动拖尾的爆发」+「点击效果」分别触发（互不依赖）。</summary>
    private void HandleMouseClick(MouseEventArgs e, bool isRight)
    {
        if (!_loaded || !IsActive || _demoMode) return;

        var position = GetPointerPosition(e);
        Trace($"{(isRight ? "rdown" : "ldown")} pos={position.X:0.#},{position.Y:0.#} "
              + $"event={e.GetPosition(this).X:0.#},{e.GetPosition(this).Y:0.#} src={e.OriginalSource?.GetType().Name}");
        TrackPointer(position);

        if (_trailEnabled)
        {
            if (isRight) _trailEngine.BoostCount();              // 右键：只加拖尾数量
            else _trailEngine.BoostSpeedAndCount();              // 左键：速度 + 数量都拉满
        }

        TriggerClickEffects(position, isRight);
        Wake();
    }

    /// <summary>
    /// 触发当前勾选的点击效果（与自动效果无关；可以一次触发多个）。
    /// 新增点击特效时：清单里加一条 Id，这里加一个受开关控制的分支，再在 <see cref="OnRender"/> 里画出来。
    /// </summary>
    private void TriggerClickEffects(Point position, bool isRight)
    {
        if (!IsActive) return;

        if (_burstEnabled) _burstEngine.Burst(position.X, position.Y,
            isRight ? ParticleBurstEngine.RightBurstCount : ParticleBurstEngine.DefaultBurstCount);
        if (_dispersionEnabled) _dispersion.Trigger(position);
        if (_flashEnabled) _flash.Trigger(position);
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
        ResetAllEffects();          // 失活时清空（对齐早期版本离开窗口就清空；回来重新开始）
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
            ResetAllEffects();
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
            Debug.WriteLine($"[ParticleOverlay] 特效帧更新失败：{ex}");
        }
    }

    private void AdvanceFrame()
    {
        if (!_loaded || !IsActive) return;

        var window = _window;
        if (window is null) return;
        if (window.WindowState == WindowState.Minimized) return;

        var generating = _demoMode || (window.IsActive && _pointerInside);
        var trailWork = _trailEnabled && generating;
        var transientWork = HasTransientWork();

        if (!trailWork && !transientWork)
        {
            // 没东西要画：把计时归零并退出渲染循环，等下一次 Wake() / 再订阅
            _idle = true;
            _lastTick = 0;
            UpdateRenderingSubscription();
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
        _effectSeconds += frameSteps / ParticleEngine.ReferenceFps;

        if (_demoMode) UpdateDemoEmitter(frameSteps);

        if (trailWork) _trailEngine.Update(frameSteps);
        if (_burstEnabled) _burstEngine.Update(frameSteps);
        if (_dispersionEnabled) _dispersion.Update(frameSteps);
        if (_flashEnabled) _flash.Update(frameSteps);

        _idle = false;
        if (_traceFrame++ % 20 == 0)
        {
            Trace($"frame emitter={_trailEngine.EmitterX:0.#},{_trailEngine.EmitterY:0.#} "
                  + $"trail={_trailEngine.Particles.Count} burst={_burstEngine.Particles.Count} ptr={_pointerInside}");
        }

        // 一次性特效播完后如果也没有自动拖尾在跑，就退订（最后一帧先画完再退订）
        if (!HasTransientWork() && !HasContinuousWork()) UpdateRenderingSubscription();

        InvalidateVisual();   // 主动重绘 → 下一帧渲染回调继续来（自维持循环）
    }

    /// <summary>
    /// 演示模式：发射点沿窗口中部一个李萨如轨迹跑，周期性触发点击特效 ——
    /// 无鼠标、无窗口激活也能截图看到「自动效果 + 点击效果」的真实样子。
    /// </summary>
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
        var emitter = new Point(
            centerX + Math.Cos(t * 1.1) * radiusX,
            centerY + Math.Sin(t * 1.5) * radiusY);

        _pointerInside = true;
        _pointerPosition = emitter;
        if (_trailEnabled) _trailEngine.SetEmitter(emitter.X, emitter.Y);

        _demoFrames++;
        if (_trailEnabled && _demoFrames % 12 == 0) _trailEngine.BoostCount();   // 周期性一小团
        if (_demoFrames % 36 == 0) TriggerClickEffects(emitter, isRight: _demoFrames % 72 == 0);   // 周期性来一次点击特效
    }

    // ================================================================== 绘制

    /// <summary>
    /// 把当前所有特效画出来（顺序：鼠标散光在最底、粒子在上、点击特效最上）。
    /// 新增特效的绘制入口也在这里加（按「底层 → 上层」排）。
    /// </summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        // 1) 自动散光：柔和圆光垫在粒子下面
        if (_glowEnabled && IsActive && (_pointerInside || _demoMode))
        {
            _cursorGlow.Center = _pointerPosition;
            _cursorGlow.Visible = true;
            _cursorGlow.Render(drawingContext, _effectSeconds);
        }
        else
        {
            _cursorGlow.Visible = false;
        }

        // 2) 自动拖尾 + 3) 点击爆炸：同一套调色板
        DrawParticles(drawingContext, _trailEngine.Particles);
        if (_burstEnabled) DrawParticles(drawingContext, _burstEngine.Particles);

        // 4) 点击散光扩散：用主色画刷当中心亮核
        if (_dispersionEnabled) _dispersion.Render(drawingContext, _coreBrushes[0, PaletteLevels - 1]);

        // 5) 点击光晕闪烁
        if (_flashEnabled) _flash.Render(drawingContext);
    }

    /// <summary>把一组粒子画成圆点：外层柔光 + 内层实心，透明度/大小随寿命衰减。</summary>
    private void DrawParticles(DrawingContext drawingContext, IReadOnlyList<Particle> particles)
    {
        for (var i = 0; i < particles.Count; i++)
        {
            var particle = particles[i];
            var radius = particle.Size * 0.5;
            if (radius <= 0.15 || particle.Alpha <= 0.01) continue;   // 快消失的粒子不值得画

            var center = new Point(particle.X, particle.Y);
            var brushIndex = AlphaBucket(particle.Alpha);
            var colorIndex = ColorBucket(particle.ColorMix);

            // 柔光只在圆点还够大时画，省掉大量"看不见的小点"的绘制开销
            if (radius >= 1.2)
            {
                var glowRadius = radius * GlowRadiusScale + 0.5;
                drawingContext.DrawEllipse(_glowBrushes[colorIndex, brushIndex], null, center, glowRadius, glowRadius);
            }

            drawingContext.DrawEllipse(_coreBrushes[colorIndex, brushIndex], null, center, radius, radius);
        }
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var overlay = (ParticleOverlay)d;
        overlay.RebuildPalettes();
        overlay.InvalidateVisual();
    }

    /// <summary>
    /// 按当前设置重建「颜色档 × 透明度档」的冻结画刷表，并同步散光 / 闪光渲染器的颜色。
    /// 开关 / 颜色一变就重建一次（不是每帧），然后重绘把上一帧擦掉。
    /// </summary>
    private void RebuildPalettes()
    {
        var (coreColors, glowColors) = ResolvePaletteColors();
        _colorCount = coreColors.Count;
        _coreBrushes = new Brush[_colorCount, PaletteLevels];
        _glowBrushes = new Brush[_colorCount, PaletteLevels];

        for (var colorIndex = 0; colorIndex < _colorCount; colorIndex++)
        {
            var core = BuildPalette(coreColors[colorIndex], 1.0);
            var glow = BuildPalette(glowColors[colorIndex], GlowMaxAlpha);
            for (var alphaIndex = 0; alphaIndex < PaletteLevels; alphaIndex++)
            {
                _coreBrushes[colorIndex, alphaIndex] = core[alphaIndex];
                _glowBrushes[colorIndex, alphaIndex] = glow[alphaIndex];
            }
        }

        // 散光 / 闪光用第一档颜色（默认 = 粒子主色；自定义双色时取颜色 1），和粒子观感一致
        var primary = coreColors[0];
        _cursorGlow.SetColor(primary);
        _flash.SetColor(primary);
    }

    /// <summary>按当前设置算出要预生成的颜色档列表（主色 + 柔光色各一份、数量相同）。</summary>
    private (List<Color> Core, List<Color> Glow) ResolvePaletteColors()
    {
        var core = new List<Color>(ColorBuckets);
        var glow = new List<Color>(ColorBuckets);

        if (!UseCustomColors || (!UseColor1 && !UseColor2))
        {
            // 没开自定义 / 两种都没勾：原来的淡紫白（主色和柔光色分开）
            core.Add(ParticleColor);
            glow.Add(GlowColor);
            return (core, glow);
        }

        if (!UseColor1 || !UseColor2)
        {
            // 只启用了一种：单色粒子（柔光用同色），不参与随机混色
            var only = UseColor1 ? Color1 : Color2;
            core.Add(only);
            glow.Add(only);
            return (core, glow);
        }

        // 两种都启用：在 Color1 → Color2 之间切 ColorBuckets 档，粒子按随机 ColorMix 落档
        for (var i = 0; i < ColorBuckets; i++)
        {
            var t = ColorBuckets <= 1 ? 0 : i / (double)(ColorBuckets - 1);
            var color = ParticleColorRange.Lerp(Color1, Color2, t);
            core.Add(color);
            glow.Add(color);
        }

        return (core, glow);
    }

    /// <summary>把粒子的随机 ColorMix 映射到颜色档下标（单色 / 默认时永远是 0）。</summary>
    private int ColorBucket(double colorMix)
    {
        if (_colorCount <= 1) return 0;
        var index = (int)Math.Round(Math.Clamp(colorMix, 0.0, 1.0) * (_colorCount - 1));
        return Math.Clamp(index, 0, _colorCount - 1);
    }

    /// <summary>
    /// 预生成一档一档的半透明画刷（全部冻结）：绘制时按 alpha 查表，**每帧零分配**。
    /// 早期版本每个粒子每帧 new 一个 SolidColorBrush，粒子上限一放开就是 GC 灾难。
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
        static bool IsOne(string? value) => string.Equals(value?.Trim(), "1", StringComparison.Ordinal);
        return IsOne(Environment.GetEnvironmentVariable("YLT_PARTICLE_DEMO"))
               || IsOne(Environment.GetEnvironmentVariable("YLT_EFFECT_DEMO"));
    }
}
