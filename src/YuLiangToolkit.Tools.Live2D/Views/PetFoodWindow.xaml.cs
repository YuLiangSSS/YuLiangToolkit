using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using YuLiangToolkit.Tools.Live2D.Controls;
using YuLiangToolkit.Tools.Live2D.Play;

namespace YuLiangToolkit.Tools.Live2D.Views;

/// <summary>
/// 投喂层（<see cref="PetFoodWindow"/>）的回调集合：由会话（Live2DSession）按这些回调接线。
/// <para>坐标约定：屏幕坐标都是 <b>DIP</b>，和 <c>Window.Left</c> / <c>Top</c> 同一坐标系
/// （GetCursorPos 那种物理像素由投喂层自己换算）。</para>
/// </summary>
public sealed class PetFoodWindowOptions
{
    /// <summary>嘴部圆心（屏幕 DIP）；返回 null = 现在不能吃（模型没加载 / 嘴部不可见 / 桌宠在说话？）。</summary>
    public Func<Point?>? GetMouthScreenPoint { get; init; }

    /// <summary>桌宠窗口的屏幕矩形（屏幕 DIP）；返回 null = 拿不到（投喂反馈的 nx/ny 会推 0）。</summary>
    public Func<Rect?>? GetPetScreenRect { get; init; }

    /// <summary>饭团掉进嘴里（参数：饭团种类、饭团中心屏幕 DIP）；会话负责涨饱食度 / 好感度 / 说话。</summary>
    public Action<RiceBallKind, Point>? OnEaten { get; init; }

    /// <summary>
    /// 靠近反馈（参数：nx、ny、strength、mouthOpen）。
    /// nx / ny 是「饭团相对桌宠窗口中心」的方向，-1 ~ 1（可用来让桌宠歪头 / 眼神跟过去）；
    /// strength = 1 - 距离/300；mouthOpen = 1 - 距离/120；没有饭团靠近时推 (0,0,0,0)。
    /// </summary>
    public Action<float, float, float, float>? OnFeedingFocus { get; init; }

    /// <summary>可选日志（物理异常 / 投喂层内部提示；会话可以接到工具页日志，方便排查）。</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// 「投喂层」：盖住主屏工作区的透明全屏窗口，里面是会被重力 / 鼠标拖拽影响的大白饭团（+ 撒出来的米粒）。
///
/// <para><b>交互</b>：左键按住饭团拖动、松手按最近轨迹甩出去；饭团中心离嘴 ≤ 34 DIP（或这一帧会穿过嘴）
/// 就算被吃掉 —— 移除饭团、原地撒一把米粒、回调 <see cref="PetFoodWindowOptions.OnEaten"/>。</para>
///
/// <para><b>透明区域点击穿透（最关键）</b>：跟 <see cref="PetWindow"/> 同款动态方案 —— 40ms 轮询
/// <c>GetCursorPos</c>，光标在任意饭团上 / 正在拖动 → 摘掉 <c>WS_EX_TRANSPARENT</c>；否则加上，
/// 鼠标直接穿透到下面的窗口。窗口本身 <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>，不抢焦点、不进
/// Alt+Tab。没有饭团 / 米粒时整个窗口 <c>Hide()</c>，绝不会一直挡着桌面。</para>
///
/// <para><b>物理循环</b>：16ms 的 DispatcherTimer，只在「有球 / 有米粒 / 正在拖」时跑（空闲自动停）；
/// 每帧调 <see cref="RiceBallPhysics.Step"/>，再把位置 / 旋转同步到 <see cref="RiceBallVisual"/>
/// （Canvas.Left = X - Radius、Top = Y - Radius，旋转走控件内部的 RotateTransform）。</para>
///
/// <para><b>多屏</b>：先只支持主屏（窗口按 <see cref="SystemParameters.WorkArea"/> 铺满主屏工作区）；
/// 桌宠跑到副屏时，饭团仍按主屏坐标投放（投喂层不覆盖副屏）。</para>
///
/// <para><b>线程</b>：Spawn / Clear 都要在 UI 线程调；内部做了 Dispatcher 兜底。</para>
/// </summary>
public sealed partial class PetFoodWindow : Window
{
    // ==================== Win32（和 PetWindow 同一套：动态点击穿透 + 不抢焦点） ====================

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    // ==================== 手感参数（想调「多近算进嘴 / 多近会凑过来」改这里） ====================

    /// <summary>嘴部命中半径（DIP）：饭团中心离嘴圆心 ≤ 34 算吃掉。</summary>
    private const double EatDistance = 34.0;

    /// <summary>靠近反馈距离（DIP）：最近饭团离嘴 &lt; 300 时桌宠会凑过来。</summary>
    private const double FocusDistance = 300.0;

    /// <summary>张嘴距离（DIP）：离嘴 120 以内开始明显张嘴（mouthOpen 从 0 涨到 1）。</summary>
    private const double MouthOpenDistance = 120.0;

    /// <summary>鼠标命中放宽（DIP）：点球边缘外一点也能抓住。</summary>
    private const double DragHitPadding = 6.0;

    /// <summary>物理循环间隔（毫秒）。</summary>
    private const int PhysicsIntervalMs = 16;

    /// <summary>点击穿透 / 光标命中轮询间隔（毫秒，与 PetWindow 一致）。</summary>
    private const int PollIntervalMs = 40;

    /// <summary>拖拽轨迹最多留几个采样点（ComputeThrowVelocity 只用最近 5 个）。</summary>
    private const int MaxDragSamples = 8;

    /// <summary>吃掉时撒多少颗米粒。</summary>
    private const int CrumbCount = 16;

    // ==================== 状态 ====================

    /// <summary>会话给的回调集合。</summary>
    private readonly PetFoodWindowOptions _options;

    /// <summary>饭团物理数据（顺序即添加顺序；后加的在上层）。</summary>
    private readonly List<RiceBallBody> _balls = new();

    /// <summary>饭团 → 自绘视觉（Canvas 子元素）。</summary>
    private readonly Dictionary<RiceBallBody, RiceBallVisual> _visuals = new();

    /// <summary>米粒数据（care-core 的物理函数负责推进 / 回收）。</summary>
    private readonly List<RiceCrumb> _crumbs = new();

    /// <summary>米粒 → 画在 Canvas 上的小圆点（寿命到了跟着回收）。</summary>
    private readonly Dictionary<RiceCrumb, Ellipse> _crumbDots = new();

    /// <summary>当前拖拽的最近轨迹（松手算抛出速度）。</summary>
    private readonly List<RiceBallDragSample> _trail = new();

    /// <summary>物理循环（16ms，空闲停）。</summary>
    private readonly DispatcherTimer _physicsTimer;

    /// <summary>点击穿透轮询（40ms，窗口可见时跑）。</summary>
    private readonly DispatcherTimer _pollTimer;

    /// <summary>统一时钟（物理 delta + 拖拽采样时间戳）。</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>随机数（投放初速度 / 米粒方向）。</summary>
    private readonly Random _random = new();

    /// <summary>上次物理帧的时间（秒），用来算 delta。</summary>
    private double _lastTickSeconds;

    /// <summary>正在拖的饭团（null = 没在拖）。</summary>
    private RiceBallBody? _dragBody;

    /// <summary>按下点与饭团中心的偏移（拖动时保持手感，不会一抓就跳到鼠标）。</summary>
    private Vector _dragOffset;

    /// <summary>上一次推给会话的靠近反馈（值没变就不重复调回调）。</summary>
    private (float Nx, float Ny, float Strength, float MouthOpen) _lastFocus;

    /// <summary>窗口已关闭（停止一切回调 / 定时器）。</summary>
    private bool _closing;

    /// <summary>最近一次设置的 WS_EX_TRANSPARENT 状态（避免每 40ms 都调 SetWindowLong）。</summary>
    private bool _clickThroughApplied;

    /// <summary>米粒小圆点用的暖白画刷。</summary>
    private static readonly SolidColorBrush CrumbBrush = CreateCrumbBrush();

    /// <summary>创建投喂层窗口（只建窗口、不显示；第一次 Spawn 时才 Show）。</summary>
    public PetFoodWindow(PetFoodWindowOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        InitializeComponent();

        // 铺满主屏工作区（DIP）：Left / Top 可以是非 0（任务栏在左 / 上时工作区有偏移）。
        // 多屏说明见类注释：先只支持主屏。
        var area = SystemParameters.WorkArea;
        Left = area.Left;
        Top = area.Top;
        Width = Math.Max(1, area.Width);
        Height = Math.Max(1, area.Height);

        _physicsTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(PhysicsIntervalMs),
        };
        _physicsTimer.Tick += OnPhysicsTick;

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs),
        };
        _pollTimer.Tick += OnPollTick;

        Loaded += OnLayerLoaded;
        Closed += OnLayerClosed;
    }

    // ==================== 对外 API（会话层用） ====================

    /// <summary>窗口里还有几颗饭团。</summary>
    public int BallCount => _balls.Count;

    /// <summary>还有没有饭团（= 投喂层该不该显示）。</summary>
    public bool HasBalls => _balls.Count > 0;

    /// <summary>
    /// 丢一颗饭团到屏幕上（<paramref name="screenPosition"/> 是屏幕 DIP，通常是鼠标位置 / 桌宠旁边）。
    /// <para>内部：转成窗口坐标 → 夹在窗口里 → 给一点随机小初速度（轻轻弹出来）→ Show 窗口并开物理循环。
    /// 窗口隐藏时调也没关系（会自动 Show）。</para>
    /// </summary>
    public void SpawnRiceBall(RiceBallKind kind, Point screenPosition)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => SpawnRiceBall(kind, screenPosition)));
            return;
        }
        if (_closing) return;

        var radius = RiceBallPhysics.GetRadius(kind);
        var local = ScreenToLocal(screenPosition);
        var areaWidth = Math.Max(radius * 2 + 4, ActualWidth > 1 ? ActualWidth : Width);
        var areaHeight = Math.Max(radius * 2 + 4, ActualHeight > 1 ? ActualHeight : Height);
        var minX = radius + 2;
        var maxX = Math.Max(minX, areaWidth - radius - 2);
        var minY = radius + 2;
        var maxY = Math.Max(minY, areaHeight - radius - 2);
        local.X = Math.Clamp(local.X, minX, maxX);
        local.Y = Math.Clamp(local.Y, minY, maxY);

        var body = new RiceBallBody
        {
            Kind = kind,
            X = local.X,
            Y = local.Y,
            // 放在桌上时是静止的（好抓）；拖动松手后按鼠标轨迹给初速度，之后才有重力弹跳。
            VelocityX = 0,
            VelocityY = 0,
            RotationDegrees = _random.NextDouble() * 360,
            AngularVelocity = 0,
        };

        var visual = new RiceBallVisual
        {
            Kind = kind,
            Width = radius * 2,
            Height = radius * 2,
            RotationDegrees = body.RotationDegrees,
        };

        _balls.Add(body);
        _visuals[body] = visual;
        FoodCanvas.Children.Add(visual);
        UpdateBallVisual(body);

        EnsureLayerVisible();
        StartPhysics();
        RefreshClickThrough();
    }

    /// <summary>清空投喂层：移除所有饭团 / 米粒、停定时器、隐藏窗口（再投喂时自动重新显示）。</summary>
    public void ClearAll()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ClearAll));
            return;
        }

        _dragBody = null;
        _trail.Clear();
        if (FoodCanvas.IsMouseCaptured) FoodCanvas.ReleaseMouseCapture();

        foreach (var visual in _visuals.Values) FoodCanvas.Children.Remove(visual);
        _visuals.Clear();
        _balls.Clear();

        foreach (var dot in _crumbDots.Values) FoodCanvas.Children.Remove(dot);
        _crumbDots.Clear();
        _crumbs.Clear();

        StopIdleLayer();
    }

    /// <summary>桌宠隐藏 / 程序退出时调：清空并隐藏投喂层（不销毁窗口，之后还能继续 Spawn）。</summary>
    public void DisposeBalls() => ClearAll();

    // ==================== 生命周期 ====================

    /// <summary>窗口第一次显示后：对齐工作区、确保「先穿透再判断」。</summary>
    private void OnLayerLoaded(object sender, RoutedEventArgs e)
    {
        SyncWorkArea();
        ApplyClickThrough(true);
        if (!_pollTimer.IsEnabled) _pollTimer.Start();
    }

    /// <summary>窗口被真正关闭（会话释放）：停定时器、清数据、把靠近反馈归零。</summary>
    private void OnLayerClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _physicsTimer.Stop();
        _pollTimer.Stop();
        _balls.Clear();
        _visuals.Clear();
        _crumbs.Clear();
        _crumbDots.Clear();
        _trail.Clear();
        _dragBody = null;
        PushFocus(0, 0, 0, 0);
    }

    /// <summary>窗口创建时就不抢焦点、不进 Alt+Tab（只动态增减 WS_EX_TRANSPARENT 一位）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetExtendedStyle(hwnd);
        style |= WsExNoActivate | WsExToolWindow;
        SetExtendedStyle(hwnd, style);

        // 首帧就穿透：窗口盖住整个工作区，绝不能先挡住鼠标
        ApplyClickThrough(true);
    }

    // ==================== 投放 / 清空辅助 ====================

    /// <summary>确保窗口显示、轮询启动（窗口隐藏时 Spawn 自动走这里）。</summary>
    private void EnsureLayerVisible()
    {
        SyncWorkArea();
        if (!IsVisible) Show();
        if (!_pollTimer.IsEnabled) _pollTimer.Start();
        // 刚显示先按光标位置算一次穿透状态（别等 40ms，否则球在鼠标下时一开始抓不住）
        RefreshClickThrough();
    }

    /// <summary>把窗口对齐主屏工作区（显示器分辨率 / 任务栏位置变了也能跟上）。</summary>
    private void SyncWorkArea()
    {
        try
        {
            var area = SystemParameters.WorkArea;
            if (Math.Abs(area.Left - Left) > 0.5 || Math.Abs(area.Top - Top) > 0.5 ||
                Math.Abs(area.Width - Width) > 0.5 || Math.Abs(area.Height - Height) > 0.5)
            {
                Left = area.Left;
                Top = area.Top;
                Width = Math.Max(1, area.Width);
                Height = Math.Max(1, area.Height);
            }
        }
        catch
        {
            // 拿不到屏幕信息（极端情况）：保持现状
        }
    }

    /// <summary>启动物理循环（重置 delta 基准，避免停久了第一帧跳一大步）。</summary>
    private void StartPhysics()
    {
        _lastTickSeconds = _clock.Elapsed.TotalSeconds;
        if (!_physicsTimer.IsEnabled) _physicsTimer.Start();
    }

    /// <summary>空闲收尾：停物理 / 轮询、隐藏窗口、靠近反馈归零（窗口绝不留着挡鼠标）。</summary>
    private void StopIdleLayer()
    {
        _physicsTimer.Stop();
        _pollTimer.Stop();
        PushFocus(0, 0, 0, 0);
        if (IsVisible) Hide();
        // 隐藏期间保持「穿透」样式：下次 Show 的第一帧不会挡住鼠标
        ApplyClickThrough(true);
    }

    // ==================== 物理循环 ====================

    /// <summary>16ms 一帧：推进物理 → 同步视觉 → 吃判定 → 米粒 → 靠近反馈 → 空闲收尾。</summary>
    private void OnPhysicsTick(object? sender, EventArgs e)
    {
        if (_closing || !IsVisible) return;
        try
        {
            StepFrame();
        }
        catch (Exception ex)
        {
            // 物理层还在实现 / 极端数值：绝不把 Dispatcher 带崩（打一行日志后回到空闲状态）
            Report("投喂层物理异常：" + ex.Message);
            ClearAll();
        }
    }

    /// <summary>一帧的实际逻辑（抽出来方便异常处理）。</summary>
    private void StepFrame()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var deltaSeconds = Math.Clamp(now - _lastTickSeconds, 0.001, 0.05);
        _lastTickSeconds = now;

        var areaWidth = Math.Max(1, ActualWidth > 1 ? ActualWidth : Width);
        var areaHeight = Math.Max(1, ActualHeight > 1 ? ActualHeight : Height);

        // ① 推进物理 + 同步视觉（拖动中的球位置由鼠标直接给，不推进）
        foreach (var body in _balls)
        {
            if (!body.IsDragging) RiceBallPhysics.Step(body, deltaSeconds, areaWidth, areaHeight);
            UpdateBallVisual(body);
        }

        // ② 吃判定：嘴部圆心（屏幕 → 窗口）；先距离，再补一次「这一帧会不会穿过嘴」的扫掠，
        //    快速甩过去也不会从嘴边漏掉。
        var mouthScreen = TryGetMouthScreenPoint();
        var mouthLocal = mouthScreen is { } mouthPoint ? ScreenToLocal(mouthPoint) : (Point?)null;
        if (mouthLocal is { } target)
        {
            foreach (var body in _balls.ToArray())
            {
                if (!body.Consumed && IsAtMouth(body, deltaSeconds, target, areaHeight)) EatBall(body);
            }
        }

        // ③ 米粒：物理推进 + 视觉回收
        RiceBallPhysics.StepCrumbs(_crumbs, deltaSeconds);
        SyncCrumbs();

        // ④ 靠近反馈（桌宠凑过来 / 张嘴 / 眼神）
        UpdateFeedingFocus(mouthLocal);

        // ⑤ 没球也没米粒了就收起窗口；否则继续保持（窗口可见时由 40ms 轮询管穿透）
        if (_balls.Count == 0 && _crumbs.Count == 0 && _dragBody is null) StopIdleLayer();
    }

    /// <summary>这一帧饭团算不算到嘴了：中心距离 ≤ 34 DIP，或扫掠线段穿过嘴部圆。</summary>
    private static bool IsAtMouth(RiceBallBody body, double deltaSeconds, Point mouth, double areaHeight)
    {
        var dx = body.X - mouth.X;
        var dy = body.Y - mouth.Y;
        if ((dx * dx) + (dy * dy) <= EatDistance * EatDistance) return true;
        if (body.IsDragging) return false;

        try
        {
            return RiceBallPhysics.TryHitMouth(
                body.X, body.Y, body.VelocityX, body.VelocityY, deltaSeconds, areaHeight,
                mouth.X, mouth.Y, EatDistance, out _);
        }
        catch (NotImplementedException)
        {
            // 物理层还没实现完 / 自检环境：退回上面的近距离判定
            return false;
        }
        catch
        {
            // 扫掠判定出错不影响继续玩
            return false;
        }
    }

    /// <summary>吃掉一颗饭团：移除视觉、原地撒米粒、回调会话（屏幕坐标）。</summary>
    private void EatBall(RiceBallBody body)
    {
        body.Consumed = true;
        var screenPoint = LocalToScreen(body.X, body.Y);

        if (_visuals.Remove(body, out var visual)) FoodCanvas.Children.Remove(visual);
        _balls.Remove(body);

        if (ReferenceEquals(_dragBody, body))
        {
            _dragBody = null;
            _trail.Clear();
            if (FoodCanvas.IsMouseCaptured) FoodCanvas.ReleaseMouseCapture();
        }

        RiceBallPhysics.SpawnCrumbs(_crumbs, body.X, body.Y, CrumbCount, _random);
        SyncCrumbs(); // 先把新米粒的圆点建出来（同样走同步逻辑，少写一份）

        try
        {
            _options.OnEaten?.Invoke(body.Kind, screenPoint);
        }
        catch (Exception ex)
        {
            // 会话的吃完回调（涨饱食度 / 说话）抛异常不影响投喂层继续玩
            Report("投喂吃掉回调异常：" + ex.Message);
        }
    }

    /// <summary>把物理位置 / 旋转同步到 Canvas 上的视觉（Center = X-Radius、Y-Radius）。</summary>
    private void UpdateBallVisual(RiceBallBody body)
    {
        if (!_visuals.TryGetValue(body, out var visual)) return;
        var radius = body.Radius;
        Canvas.SetLeft(visual, body.X - radius);
        Canvas.SetTop(visual, body.Y - radius);
        visual.RotationDegrees = body.RotationDegrees;
    }

    // ==================== 米粒 ====================

    /// <summary>按米饭粒子列表补齐 / 回收 Canvas 上的小圆点（StepCrumbs 会移除寿命到的粒子）。</summary>
    private void SyncCrumbs()
    {
        if (_crumbDots.Count > 0)
        {
            foreach (var pair in _crumbDots.ToArray())
            {
                var crumb = pair.Key;
                var dot = pair.Value;
                if (crumb.Life <= 0 || !_crumbs.Contains(crumb))
                {
                    FoodCanvas.Children.Remove(dot);
                    _crumbDots.Remove(crumb);
                    continue;
                }

                dot.Opacity = Math.Clamp(crumb.Opacity, 0, 1);
                Canvas.SetLeft(dot, crumb.X - crumb.Size);
                Canvas.SetTop(dot, crumb.Y - crumb.Size);
            }
        }

        // 新生成的米粒（刚爆出来 / 还没建圆点的）补上圆点
        if (_crumbDots.Count == _crumbs.Count) return;
        foreach (var crumb in _crumbs)
        {
            if (_crumbDots.ContainsKey(crumb)) continue;
            AddCrumbDot(crumb);
        }
    }

    /// <summary>给一颗米粒建小圆点（暖白色，寿命后段由 Opacity 淡出）。</summary>
    private void AddCrumbDot(RiceCrumb crumb)
    {
        var dot = new Ellipse
        {
            Width = Math.Max(1, crumb.Size * 2),
            Height = Math.Max(1, crumb.Size * 2),
            Fill = CrumbBrush,
            IsHitTestVisible = false,
            Opacity = Math.Clamp(crumb.Opacity, 0, 1),
        };
        Canvas.SetLeft(dot, crumb.X - crumb.Size);
        Canvas.SetTop(dot, crumb.Y - crumb.Size);
        FoodCanvas.Children.Add(dot);
        _crumbDots[crumb] = dot;
    }

    // ==================== 靠近反馈 ====================

    /// <summary>
    /// 找离嘴最近的饭团：距离 &lt; 300 DIP 就把方向（nx / ny，相对桌宠窗口中心，-1 ~ 1）、
    /// strength、mouthOpen 推给会话；没有 / 太远推 (0,0,0,0)。值没变不重复调回调。
    /// </summary>
    private void UpdateFeedingFocus(Point? mouthLocal)
    {
        if (mouthLocal is not { } mouth)
        {
            PushFocus(0, 0, 0, 0);
            return;
        }

        RiceBallBody? nearest = null;
        var bestDistanceSquared = double.MaxValue;
        foreach (var body in _balls)
        {
            var dx = body.X - mouth.X;
            var dy = body.Y - mouth.Y;
            var distanceSquared = (dx * dx) + (dy * dy);
            if (distanceSquared >= bestDistanceSquared) continue;
            bestDistanceSquared = distanceSquared;
            nearest = body;
        }

        if (nearest is null)
        {
            PushFocus(0, 0, 0, 0);
            return;
        }

        var distance = Math.Sqrt(bestDistanceSquared);
        if (distance >= FocusDistance)
        {
            PushFocus(0, 0, 0, 0);
            return;
        }

        // nx / ny：相对桌宠窗口中心的方向（拿不到桌宠矩形就只推远近，不推方向）
        var nx = 0f;
        var ny = 0f;
        var petRect = TryGetPetScreenRect();
        if (petRect is { Width: > 0.5, Height: > 0.5 } rect)
        {
            var ballScreen = LocalToScreen(nearest.X, nearest.Y);
            var centerX = rect.Left + (rect.Width / 2);
            var centerY = rect.Top + (rect.Height / 2);
            nx = (float)Math.Clamp((ballScreen.X - centerX) / (rect.Width / 2), -1, 1);
            ny = (float)Math.Clamp((ballScreen.Y - centerY) / (rect.Height / 2), -1, 1);
        }

        var strength = (float)Math.Clamp(1 - (distance / FocusDistance), 0, 1);
        var mouthOpen = (float)Math.Clamp(1 - (distance / MouthOpenDistance), 0, 1);
        PushFocus(nx, ny, strength, mouthOpen);
    }

    /// <summary>推靠近反馈（变化小于 0.01 不重复调；回调异常吞掉）。</summary>
    private void PushFocus(float nx, float ny, float strength, float mouthOpen)
    {
        if (Math.Abs(nx - _lastFocus.Nx) < 0.01f &&
            Math.Abs(ny - _lastFocus.Ny) < 0.01f &&
            Math.Abs(strength - _lastFocus.Strength) < 0.01f &&
            Math.Abs(mouthOpen - _lastFocus.MouthOpen) < 0.01f)
        {
            return;
        }

        _lastFocus = (nx, ny, strength, mouthOpen);
        try
        {
            _options.OnFeedingFocus?.Invoke(nx, ny, strength, mouthOpen);
        }
        catch (Exception ex)
        {
            Report("投喂靠近回调异常：" + ex.Message);
        }
    }

    // ==================== 鼠标：拖拽 / 甩出去 ====================

    /// <summary>左键按下：命中最近的饭团（后加的在上层优先）就开始拖。</summary>
    private void OnFoodMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _closing) return;

        var point = e.GetPosition(FoodCanvas);
        for (var i = _balls.Count - 1; i >= 0; i--)
        {
            var body = _balls[i];
            var dx = point.X - body.X;
            var dy = point.Y - body.Y;
            var hitRadius = body.Radius + DragHitPadding;
            if ((dx * dx) + (dy * dy) > hitRadius * hitRadius) continue;

            _dragBody = body;
            body.IsDragging = true;
            body.VelocityX = 0;
            body.VelocityY = 0;
            body.AngularVelocity = 0;
            _dragOffset = new Vector(body.X - point.X, body.Y - point.Y);
            _trail.Clear();
            _trail.Add(new RiceBallDragSample(body.X, body.Y, _clock.Elapsed.TotalSeconds));

            FoodCanvas.CaptureMouse();
            ApplyClickThrough(false);
            StartPhysics();
            e.Handled = true;
            return;
        }
    }

    /// <summary>拖动：饭团中心跟鼠标（保留按下偏移），记录轨迹并让饭团跟着滚一点。</summary>
    private void OnFoodMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragBody is not { } body) return;

        var point = e.GetPosition(FoodCanvas);
        var areaWidth = Math.Max(1, ActualWidth > 1 ? ActualWidth : Width);
        var areaHeight = Math.Max(1, ActualHeight > 1 ? ActualHeight : Height);
        var radius = body.Radius;
        var previousX = body.X;

        body.X = Math.Clamp(point.X + _dragOffset.X, radius, Math.Max(radius, areaWidth - radius));
        body.Y = Math.Clamp(point.Y + _dragOffset.Y, radius, Math.Max(radius, areaHeight - radius));
        // 拖着走的时候让饭团跟着滚一点（手感；松手后由物理接管角速度）
        body.RotationDegrees += (body.X - previousX) * 0.55;

        _trail.Add(new RiceBallDragSample(body.X, body.Y, _clock.Elapsed.TotalSeconds));
        if (_trail.Count > MaxDragSamples) _trail.RemoveAt(0);

        UpdateBallVisual(body);
        e.Handled = true;
    }

    /// <summary>松手：按最近轨迹算抛出速度（甩出去），继续交给物理。</summary>
    private void OnFoodMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragBody is not { } body) return;

        _trail.Add(new RiceBallDragSample(body.X, body.Y, _clock.Elapsed.TotalSeconds));
        var (velocityX, velocityY) = ComputeThrowVelocity();
        body.VelocityX = velocityX;
        body.VelocityY = velocityY;
        body.AngularVelocity = Math.Clamp(velocityX * 0.18, -540, 540);
        body.IsDragging = false;

        _dragBody = null;
        _trail.Clear();
        if (FoodCanvas.IsMouseCaptured) FoodCanvas.ReleaseMouseCapture();

        StartPhysics();
        RefreshClickThrough();
        e.Handled = true;
    }

    /// <summary>算抛出速度（物理层还在实现时退回 0：饭团停在原地，不崩）。</summary>
    private (double VelocityX, double VelocityY) ComputeThrowVelocity()
    {
        try
        {
            return RiceBallPhysics.ComputeThrowVelocity(_trail, _clock.Elapsed.TotalSeconds, 5);
        }
        catch (NotImplementedException)
        {
            return (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    // ==================== 点击穿透（和 PetWindow 同一套思路） ====================

    /// <summary>40ms 一次：光标在饭团上 / 正在拖 → 可点；否则 WS_EX_TRANSPARENT 穿透到下面的窗口。</summary>
    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_closing || !IsVisible) return;
        RefreshClickThrough();
    }

    /// <summary>按当前光标位置刷新点击穿透状态。</summary>
    private void RefreshClickThrough()
    {
        if (_closing || !IsVisible) return;
        ApplyClickThrough(!IsCursorOverBallOrDragging());
    }

    /// <summary>光标是不是正压在任意一颗饭团上（或正在拖动）。</summary>
    private bool IsCursorOverBallOrDragging()
    {
        if (_dragBody is not null) return true;
        if (_balls.Count == 0) return false;
        if (!TryGetCursorDip(out var cursor)) return false;

        var local = ScreenToLocal(cursor);
        foreach (var body in _balls)
        {
            var dx = local.X - body.X;
            var dy = local.Y - body.Y;
            var hitRadius = body.Radius + DragHitPadding;
            if ((dx * dx) + (dy * dy) <= hitRadius * hitRadius) return true;
        }
        return false;
    }

    /// <summary>开 / 关 WS_EX_TRANSPARENT（true = 鼠标穿透到下面的窗口）。</summary>
    private void ApplyClickThrough(bool enabled)
    {
        if (_clickThroughApplied == enabled) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        _clickThroughApplied = enabled;
        var style = GetExtendedStyle(hwnd);
        style |= WsExNoActivate | WsExToolWindow;
        if (enabled) style |= WsExTransparent;
        else style &= ~WsExTransparent;
        SetExtendedStyle(hwnd, style);

        // 扩展样式变化不会自动刷新窗口框架，保险起见 SWP_FRAMECHANGED 一次（不移动 / 不激活 / 不改 Z 序）
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    // ==================== 坐标 / 小工具 ====================

    /// <summary>屏幕 DIP → 窗口内坐标（窗口左上角就是工作区左上角）。</summary>
    private Point ScreenToLocal(Point screenPoint) => new(screenPoint.X - Left, screenPoint.Y - Top);

    /// <summary>窗口内坐标 → 屏幕 DIP。</summary>
    private Point LocalToScreen(double x, double y) => new(Left + x, Top + y);

    /// <summary>取鼠标屏幕坐标（DIP）：GetCursorPos 是物理像素，按窗口 DPI 换算成和 Left/Top 同一坐标系。</summary>
    private bool TryGetCursorDip(out Point point)
    {
        point = default;
        if (!GetCursorPos(out var native)) return false;

        var source = PresentationSource.FromVisual(this);
        var matrix = source?.CompositionTarget?.TransformFromDevice;
        point = matrix is { } transform
            ? transform.Transform(new Point(native.X, native.Y))
            : new Point(native.X, native.Y);
        return true;
    }

    /// <summary>取嘴部圆心（会话回调出异常按「现在不能吃」处理，别把投喂层带崩）。</summary>
    private Point? TryGetMouthScreenPoint()
    {
        try
        {
            return _options.GetMouthScreenPoint?.Invoke();
        }
        catch (Exception ex)
        {
            Report("投喂层取嘴部坐标异常：" + ex.Message);
            return null;
        }
    }

    /// <summary>取桌宠窗口屏幕矩形（回调出异常按「拿不到」处理）。</summary>
    private Rect? TryGetPetScreenRect()
    {
        try
        {
            return _options.GetPetScreenRect?.Invoke();
        }
        catch (Exception ex)
        {
            Report("投喂层取桌宠矩形异常：" + ex.Message);
            return null;
        }
    }

    /// <summary>写一行日志（没接日志回调就丢掉）。</summary>
    private void Report(string message)
    {
        try
        {
            _options.Log?.Invoke(message);
        }
        catch
        {
            // 日志回调自己出错不能影响投喂层
        }
    }

    /// <summary>米粒画刷（暖白，冻结）。</summary>
    private static SolidColorBrush CreateCrumbBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xF2, 0xFC, 0xF6, 0xE4));
        brush.Freeze();
        return brush;
    }

    // ==================== Win32 辅助（与 PetWindow 同款） ====================

    private static long GetExtendedStyle(IntPtr hwnd)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, GwlExStyle).ToInt64()
            : GetWindowLong32(hwnd, GwlExStyle);

    private static void SetExtendedStyle(IntPtr hwnd, long style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GwlExStyle, new IntPtr(style));
        else SetWindowLong32(hwnd, GwlExStyle, unchecked((int)style));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
