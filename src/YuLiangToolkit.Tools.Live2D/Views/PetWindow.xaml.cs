using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YuLiangToolkit.Tools.Live2D.Model;
using YuLiangToolkit.Tools.Live2D.Rendering;

namespace YuLiangToolkit.Tools.Live2D.Views;

/// <summary>
/// 桌面宠物窗口：透明、无边框、置顶（可关）、不进任务栏；显示 Live2D 模型当前帧。
///
/// <para><b>交互</b>：左键拖动移动、滚轮缩放宽度（160~1000，高度按模型比例算）、右键菜单
/// （置顶 / 点击穿透 / 缩放重置 / 居中 / 打开测试页 / 隐藏）、双击随机来个表情或动作。
/// 位置 / 宽度 / 不透明度 / 置顶 / 穿透都写回 <see cref="Live2DConfig"/> 自动保存。</para>
///
/// <para><b>透明区域点击穿透</b>：配置开启时用 40ms 的轮询（全局鼠标位置 → 窗口内 → 画布坐标 →
/// 可见网格包围盒）判断鼠标下是不是模型；不在模型上就动态给 HWND 加 <c>WS_EX_TRANSPARENT</c>
/// 让鼠标穿透到下面的窗口，回到模型上再摘掉。窗口本身不抢焦点（<c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>）。</para>
///
/// <para><b>渲染</b>：帧循环在 <see cref="Live2DSession"/>（与工具页共用一次 Tick）。
/// 本窗口用会话给的「桌宠专用」渲染器，不会和工具页预览的位图互相覆盖。</para>
///
/// <para><b>截图调试</b>：环境变量 <c>YLT_LIVE2D_SHOT_PET=&lt;png&gt;</c> 时，窗口出来并渲染出第一帧后
/// 约 1.5 秒把窗口内容（透明底垫深色）存成 PNG，然后退出程序（Lead / 自检成员冒烟用）。</para>
/// </summary>
public partial class PetWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>宽度范围（像素）。</summary>
    private const double MinPetWidth = 160;
    private const double MaxPetWidth = 1000;

    /// <summary>首次显示 / 重置位置时距主屏工作区右下角的边距。</summary>
    private const double DefaultMargin = 24;

    /// <summary>拖出屏幕后至少保留的可见尺寸，保证还能用鼠标抓回来。</summary>
    private const double MinVisibleWidth = 80;
    private const double MinVisibleHeight = 40;

    /// <summary>轮询周期（毫秒）：点击穿透命中测试 + 眼睛跟随全局鼠标。</summary>
    private const int PollIntervalMs = 40;

    private readonly Live2DSession _session;
    private readonly Rendering.RenderOptions _renderOptions = new() { TransparentBackground = true, FitCanvas = true };
    private readonly DispatcherTimer _pollTimer;

    private WriteableBitmap? _lastBitmap;
    private int _renderedFrames;
    private bool _shutdown;
    private bool _captureScheduled;

    // 拖动
    private bool _dragging;
    private double _dragStartLeft;
    private double _dragStartTop;
    private Point _dragStartCursor;

    // 点击穿透样式状态（避免每 40ms 都调 SetWindowLong）
    private bool _clickThroughApplied;

    // 是否正在用全局鼠标控制桌宠眼神（眼控关掉 / 鼠标取不到时清一次目标）
    private bool _eyeTargetSet;

    public PetWindow(Live2DSession session)
    {
        _session = session;
        InitializeComponent();

        var cfg = Config;
        var width = Math.Clamp(cfg?.PetWidth ?? 420, MinPetWidth, MaxPetWidth);
        Width = width;
        Height = width / Math.Max(0.05, _session.CanvasAspect);
        if (cfg is not null && cfg.PetLeft is { } savedLeft && cfg.PetTop is { } savedTop)
        {
            Left = savedLeft;
            Top = savedTop;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - DefaultMargin;
            Top = area.Bottom - Height - DefaultMargin;
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs),
        };
        _pollTimer.Tick += OnPollTick;

        Loaded += OnLoaded;
        Closed += OnClosed;

        ApplyConfigNow();
    }

    /// <summary>当前配置（工具页 ViewModel 还没建时返回 null，此时用默认值）。</summary>
    private Live2DConfig? Config => _session.ViewModel?.Cfg;

    // ===================== 对外 API =====================

    /// <summary>配置变化后重新套用外观（大小 / 不透明度 / 置顶 / 点击穿透）。</summary>
    public void RefreshFromConfig() => ApplyConfigNow();

    /// <summary>居中到主屏工作区。</summary>
    public void CenterOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
        ClampToVisibleArea();
        SavePlacement();
    }

    /// <summary>回到默认位置（主屏右下角）并恢复默认宽度。</summary>
    public void ResetPositionToDefault()
    {
        if (Config is { } cfg) cfg.PetWidth = 420;
        ApplySizeFromConfig();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - DefaultMargin;
        Top = area.Bottom - Height - DefaultMargin;
        ClampToVisibleArea();
        SavePlacement();
    }

    /// <summary>窗口尺寸 / 位置因屏幕变化越界时拉回可见区域。</summary>
    public void EnsureOnScreen()
    {
        if (Width <= 0 || Height <= 0) return;
        ClampToVisibleArea();
    }

    /// <summary>程序退出 / 隐藏桌宠前调用：停定时器并标记不再截图。</summary>
    public void CloseForShutdown()
    {
        _shutdown = true;
        try { _pollTimer.Stop(); } catch { /* 忽略 */ }
    }

    // ===================== 屏幕上的帧渲染（由会话帧循环调用） =====================

    /// <summary>用运行时当前帧画桌宠画面（会话在 Tick 之后调用）。</summary>
    public void RenderFrame(ModelRuntime runtime)
    {
        if (_shutdown || !IsVisible) return;

        var (width, height) = GetRenderTargetSize();
        if (width < 2 || height < 2) return;

        // 桌宠用会话建的独立渲染器（共享只读纹理）；创建失败时退回运行时渲染器
        var renderer = _session.PetRenderer ?? runtime.Renderer;

        var options = _renderOptions;
        options.Width = width;
        options.Height = height;
        options.FitCanvas = true;
        options.TransparentBackground = true;

        renderer.Render(runtime.Model, options);

        var bitmap = renderer.Bitmap;
        if (!ReferenceEquals(_lastBitmap, bitmap))
        {
            _lastBitmap = bitmap;
            PetImage.Source = bitmap;
        }

        // 全身轻晃：以脚为支点旋转模型位图（仅 RenderTransform；窗口尺寸 / 透明区域 / 点击穿透 / 拖动都不受影响）
        double leanDegrees = 0;
        if (_session.ViewModel?.LeanOverrideDegrees is { } fixedLean)
        {
            leanDegrees = fixedLean;
        }
        else
        {
            try { leanDegrees = runtime.BodyLeanDegrees; } catch { leanDegrees = 0; }
        }
        PetLeanTransform.Angle = Math.Clamp(leanDegrees, -30, 30);
        _renderedFrames++;
    }

    /// <summary>渲染目标像素尺寸（窗口 DIP × 渲染倍率）。</summary>
    private (int Width, int Height) GetRenderTargetSize()
    {
        var scale = Math.Clamp(Config?.RenderScale ?? 1.0, 0.5, 1.5);
        return (Math.Max(2, (int)Math.Round(ActualWidth * scale)),
            Math.Max(2, (int)Math.Round(ActualHeight * scale)));
    }

    // ===================== 生命周期 =====================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureOnScreen();
        ApplyConfigNow();
        _pollTimer.Start();

        // 开发调试：YLT_LIVE2D_SHOT_PET=<png> → 出来并渲染出画面后存图再退出
        var shotPath = _session.PetDebugShotPath;
        if (shotPath.Length > 0) ScheduleDebugCapture(shotPath);

        // 开发调试：桌宠交互自测（拖动写回配置 / 点击穿透样式 / 全局鼠标眼控），结果写 Data\live2d\pet-selftest.log
        if (Environment.GetEnvironmentVariable("YLT_LIVE2D_PET_SELFTEST") is "1" or "on" or "true")
        {
            _ = RunSelfTestAsync();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _shutdown = true;
        try { _pollTimer.Stop(); } catch { /* 忽略 */ }
        PetImage.Source = null;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 第一帧起就不抢焦点、不进 Alt+Tab（只移除/添加 WS_EX_TRANSPARENT 那一位）
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetExtendedStyle(hwnd);
        style |= WsExNoActivate | WsExToolWindow;
        SetExtendedStyle(hwnd, style);
    }

    // ===================== 配置套用 =====================

    /// <summary>把配置里的外观项套到窗口上（每 40ms 轮询里也会调，页面滑条一动桌宠就跟着变）。</summary>
    private void ApplyConfigNow()
    {
        var cfg = Config;
        if (cfg is null) return;

        var opacity = Math.Clamp(cfg.PetOpacity, 0.2, 1);
        if (Math.Abs(Opacity - opacity) > 0.001) Opacity = opacity;

        if (Topmost != cfg.PetTopMost) Topmost = cfg.PetTopMost;

        ApplySizeFromConfig();
    }

    /// <summary>按配置宽度 + 模型画布比例重算窗口高宽（保持窗口中心不动）。</summary>
    private void ApplySizeFromConfig()
    {
        var cfg = Config;
        if (cfg is null) return;

        var width = Math.Clamp(cfg.PetWidth, MinPetWidth, MaxPetWidth);
        var height = width / Math.Max(0.05, _session.CanvasAspect);
        if (Math.Abs(Width - width) < 0.5 && Math.Abs(Height - height) < 0.5) return;

        var centerX = Left + Width / 2;
        var centerY = Top + Height / 2;
        Width = width;
        Height = height;
        if (!double.IsNaN(centerX) && !double.IsNaN(centerY))
        {
            Left = centerX - width / 2;
            Top = centerY - height / 2;
        }
        ClampToVisibleArea();
    }

    // ===================== 鼠标：拖动 / 滚轮 / 双击 =====================

    private void OnPetMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TryGetCursorDip(out var cursor)) return;

        _dragging = true;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragStartCursor = cursor;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnPetMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!TryGetCursorDip(out var cursor))
        {
            FinishDrag();
            return;
        }

        Left = _dragStartLeft + (cursor.X - _dragStartCursor.X);
        Top = _dragStartTop + (cursor.Y - _dragStartCursor.Y);
        e.Handled = true;
    }

    private void OnPetMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        FinishDrag();
        e.Handled = true;
    }

    private void FinishDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        ClampToVisibleArea();
        SavePlacement();
    }

    private void OnPetMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var cfg = Config;
        if (cfg is null) return;

        var step = e.Delta > 0 ? 20 : -20;
        var width = Math.Clamp(cfg.PetWidth + step, MinPetWidth, MaxPetWidth);
        if (Math.Abs(width - cfg.PetWidth) < 0.1) return;

        cfg.PetWidth = width;
        ApplySizeFromConfig();
        SavePlacement();
    }

    private void OnPetMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var runtime = _session.Runtime;
        if (runtime?.IsLoaded != true) return;
        e.Handled = true;

        try
        {
            var viewModel = _session.ViewModel;
            var fade = Math.Clamp(Config?.ExpressionFadeSeconds ?? 0.3, 0, 2);

            // 随机表情 / 随机动作（桌宠互动）
            if (runtime.Expressions.Count > 0 && Random.Shared.Next(2) == 0)
            {
                var expression = runtime.Expressions[Random.Shared.Next(runtime.Expressions.Count)];
                if (viewModel is not null)
                {
                    // 官方语义：同一时间只有一个表情 —— 走 ViewModel 的「切换成这一个」逻辑，工具页高亮也跟着变
                    viewModel.ActivateSingleExpression(expression, fade);
                }
                else
                {
                    _session.ClearExpressions(fade);
                    _session.ApplyExpression(expression, fade);
                }
                viewModel?.Log($"桌宠互动：随机表情「{expression.Name}」");
            }
            else if (runtime.Motions.Count > 0)
            {
                var motion = runtime.Motions[Random.Shared.Next(runtime.Motions.Count)];
                _session.PlayMotion(motion, motion.Loop, Config?.MotionFadeSeconds ?? 0.3);
                viewModel?.Log($"桌宠互动：随机动作「{motion.Name}」");
            }
        }
        catch { /* 互动失败不影响桌宠 */ }
    }

    // ===================== 轮询：点击穿透 + 眼睛跟随 =====================

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_shutdown) return;

        ApplyConfigNow();
        UpdateCursorInteraction();
    }

    /// <summary>
    /// 40ms 一次：取全局鼠标位置 → 窗口内坐标 → 画布坐标。
    /// ① 眼睛跟随：跟的是**全局鼠标**（鼠标在屏幕任何位置都换算成画布坐标，超出窗口也照常跟，
    ///    只是把目标夹在画布外扩 30% 内，避免角度贴边抖动）；页面预览正在控制眼神时页面优先。
    /// ② 点击穿透：只有鼠标真的落在窗口内时才判断是否在模型上，动态开关 WS_EX_TRANSPARENT。
    /// </summary>
    private void UpdateCursorInteraction()
    {
        var cfg = Config;
        if (cfg is null) return;

        if (!TryGetCursorDip(out var cursor))
        {
            // 取不到鼠标（远程桌面 / 权限异常）：别留旧目标，穿透也先关掉
            ClearPetEyeTargetIfNeeded();
            ApplyClickThrough(false);
            return;
        }

        var localX = cursor.X - Left;
        var localY = cursor.Y - Top;
        var inside = localX >= 0 && localY >= 0 && localX <= Width && localY <= Height;
        var hasCanvas = ComputeCanvasPoint(localX, localY, out var canvasX, out var canvasY);

        // ① 眼睛跟随全局鼠标（inside 只用于下面的点击穿透判断，不再门控眼控）
        if (cfg.EyeTracking && hasCanvas)
        {
            ClampCanvasTarget(ref canvasX, ref canvasY);
            _session.SetPetEyeTarget(canvasX, canvasY);
            _eyeTargetSet = true;
        }
        else
        {
            ClearPetEyeTargetIfNeeded();
        }

        // ② 点击穿透
        if (!cfg.PetClickThrough || _dragging)
        {
            ApplyClickThrough(false);
            return;
        }

        var overModel = inside && hasCanvas && IsCanvasPointOverModel(canvasX, canvasY);
        ApplyClickThrough(!overModel);
    }

    /// <summary>眼控关掉 / 鼠标取不到时清一次目标（别每 40ms 重复调）。</summary>
    private void ClearPetEyeTargetIfNeeded()
    {
        if (!_eyeTargetSet) return;
        _eyeTargetSet = false;
        _session.ClearPetEyeTarget();
    }

    /// <summary>把眼控目标夹在画布外扩 30% 的矩形内：鼠标在屏幕很远的地方时角度不会贴边猛转 / 抖动。</summary>
    private void ClampCanvasTarget(ref float canvasX, ref float canvasY)
    {
        var runtime = _session.Runtime;
        if (runtime?.IsLoaded != true) return;
        try
        {
            var model = runtime.Model;
            var marginX = model.CanvasWidthPixels * 0.3f;
            var marginY = model.CanvasHeightPixels * 0.3f;
            canvasX = Math.Clamp(canvasX, -marginX, model.CanvasWidthPixels + marginX);
            canvasY = Math.Clamp(canvasY, -marginY, model.CanvasHeightPixels + marginY);
        }
        catch
        {
            // 模型还没准备好：不夹
        }
    }

    /// <summary>窗口内坐标（DIP）→ 模型画布像素坐标（桌宠固定「适应窗口」）。</summary>
    private bool ComputeCanvasPoint(double localX, double localY, out float canvasX, out float canvasY)
    {
        canvasX = canvasY = 0;
        var runtime = _session.Runtime;
        if (runtime?.IsLoaded != true) return false;

        try
        {
            var model = runtime.Model;
            var width = Math.Max(1.0, ActualWidth);
            var height = Math.Max(1.0, ActualHeight);
            var canvasWidth = Math.Max(1.0, model.CanvasWidthPixels);
            var canvasHeight = Math.Max(1.0, model.CanvasHeightPixels);
            var fit = Math.Min(width / canvasWidth, height / canvasHeight);
            if (fit <= 0.0001) return false;

            var offsetX = (width - canvasWidth * fit) / 2;
            var offsetY = (height - canvasHeight * fit) / 2;
            canvasX = (float)((localX - offsetX) / fit);
            canvasY = (float)((localY - offsetY) / fit);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>画布坐标是否落在「当前可见且不透明」的网格包围盒里（近似判断，不用读位图 alpha）。</summary>
    private bool IsCanvasPointOverModel(float canvasX, float canvasY)
    {
        var runtime = _session.Runtime;
        if (runtime?.IsLoaded != true) return false;

        try
        {
            foreach (var drawable in runtime.Model.Drawables)
            {
                if (!drawable.IsVisible || drawable.Opacity < 0.02f) continue;
                if (!drawable.TryGetCanvasBounds(out var minX, out var minY, out var maxX, out var maxY)) continue;
                if (canvasX >= minX && canvasX <= maxX && canvasY >= minY && canvasY <= maxY) return true;
            }
        }
        catch
        {
            // 忽略：拿不到模型数据时按「不透明」处理（宁可挡住也不错开）
            return true;
        }
        return false;
    }

    // ===================== 点击穿透（Win32） =====================

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

    // ===================== 位置 =====================

    private void SavePlacement()
    {
        var cfg = Config;
        if (cfg is null) return;
        if (double.IsFinite(Left)) cfg.PetLeft = Left;
        if (double.IsFinite(Top)) cfg.PetTop = Top;
        _session.SaveConfigNow();
    }

    /// <summary>把窗口夹在虚拟屏幕里（多显示器），至少留 80×40 可见，防止拖丢。</summary>
    private void ClampToVisibleArea()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top)) return;
        if (Width <= 0 || Height <= 0) return;

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        var minLeft = screenLeft - Width + MinVisibleWidth;
        var maxLeft = screenRight - MinVisibleWidth;
        var minTop = screenTop - Height + MinVisibleHeight;
        var maxTop = screenBottom - MinVisibleHeight;

        Left = Math.Clamp(Left, minLeft, maxLeft);
        Top = Math.Clamp(Top, minTop, maxTop);
    }

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

    // ===================== 右键菜单 =====================

    private void OnPetMenuOpened(object sender, RoutedEventArgs e)
    {
        var cfg = Config;
        if (cfg is null) return;
        MenuItemTopMost.IsChecked = cfg.PetTopMost;
        MenuItemClickThrough.IsChecked = cfg.PetClickThrough;
        MenuItemMocap.IsChecked = cfg.MocapEnabled;
        MenuItemSway.IsChecked = cfg.BodySwayEnabled;
        MenuItemMusic.IsChecked = cfg.MusicReactiveEnabled;
    }

    /// <summary>右键菜单：开 / 关音乐跟唱（WASAPI 回环采集系统声音；关掉立刻释放采集）。</summary>
    private void OnMenuMusicClick(object sender, RoutedEventArgs e)
    {
        var cfg = Config;
        if (cfg is null) return;

        cfg.MusicReactiveEnabled = !cfg.MusicReactiveEnabled;
        _session.ApplyRuntimeSettings();
        _session.ApplyMusicSettings();
        _session.SaveConfigNow();
        _session.ViewModel?.RefreshAllBindings();
        _session.Log(cfg.MusicReactiveEnabled
            ? "桌宠菜单：已开启音乐跟唱（WASAPI 回环采集系统正在播放的声音）"
            : "桌宠菜单：已关闭音乐跟唱（音频采集已释放）");
    }

    /// <summary>右键菜单：开 / 关摄像头动捕（会请求摄像头权限；关掉立刻释放摄像头）。</summary>
    private void OnMenuMocapClick(object sender, RoutedEventArgs e)
    {
        var cfg = Config;
        if (cfg is null) return;

        cfg.MocapEnabled = !cfg.MocapEnabled;
        _session.ApplyRuntimeSettings();
        _session.ApplyMocapSettings();
        _session.SaveConfigNow();
        _session.ViewModel?.RefreshAllBindings();
        _session.Log(cfg.MocapEnabled
            ? "桌宠菜单：已开启摄像头动捕（会请求摄像头权限）"
            : "桌宠菜单：已关闭摄像头动捕（摄像头已释放）");
    }

    /// <summary>右键菜单：重新校准动捕表情（眨眼 / 张嘴的自动基准清零）。</summary>
    private void OnMenuResetMocapCalibrationClick(object sender, RoutedEventArgs e)
        => _session.ResetMocapCalibration();

    /// <summary>右键菜单：开 / 关身体晃动。</summary>
    private void OnMenuSwayClick(object sender, RoutedEventArgs e)
    {
        var cfg = Config;
        if (cfg is null) return;

        cfg.BodySwayEnabled = !cfg.BodySwayEnabled;
        _session.ApplyRuntimeSettings();
        _session.SaveConfigNow();
        _session.ViewModel?.RefreshAllBindings();
        _session.Log(cfg.BodySwayEnabled ? "桌宠菜单：已开启身体晃动" : "桌宠菜单：已关闭身体晃动");
    }

    private void OnMenuTopMostClick(object sender, RoutedEventArgs e)
    {
        var cfg = Config;
        if (cfg is null) return;
        cfg.PetTopMost = !cfg.PetTopMost;
        Topmost = cfg.PetTopMost;
        _session.SaveConfigNow();
    }

    private void OnMenuClickThroughClick(object sender, RoutedEventArgs e)
    {
        var cfg = Config;
        if (cfg is null) return;
        cfg.PetClickThrough = !cfg.PetClickThrough;
        if (!cfg.PetClickThrough) ApplyClickThrough(false);
        _session.SaveConfigNow();
    }

    private void OnMenuResetSizeClick(object sender, RoutedEventArgs e)
    {
        var cfg = Config;
        if (cfg is null) return;
        cfg.PetWidth = 420;
        ApplySizeFromConfig();
        SavePlacement();
    }

    private void OnMenuCenterClick(object sender, RoutedEventArgs e) => _session.CenterPet();

    private void OnMenuOpenToolPageClick(object sender, RoutedEventArgs e)
    {
        // 工具工程引用不到外壳的 MainViewModel（主程序引用工具，反向不行），这里只负责把主窗口叫到前面，
        // 用户从侧边栏「桌面 → Live2D 桌宠」进测试页；日志里给一句提示。
        try
        {
            if (Application.Current?.MainWindow is { } mainWindow)
            {
                if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                _session.Log("已把主窗口叫到前面：侧边栏「桌面 → Live2D 桌宠」即测试页");
            }
        }
        catch (Exception ex)
        {
            _session.Log("打开测试页失败：" + ex.Message);
        }
    }

    private void OnMenuHideClick(object sender, RoutedEventArgs e) => _session.HidePet();

    // ===================== 开发调试：桌宠交互自测 =====================

    /// <summary>
    /// 开发调试（环境变量 <c>YLT_LIVE2D_PET_SELFTEST=1</c>）：用代码走一遍截图看不出来的交互 ——
    /// 「窗口高宽按模型比例 / 拖动结束写回配置 / 点击穿透开关扩展样式 / 全局鼠标眼控真的改了参数 /
    /// 眼控目标夹取」，逐项结果写进 <c>Data\live2d\pet-selftest.log</c>（另打一行到工具页日志），
    /// 方便 Lead / verify 冒烟复核；加 <c>YLT_LIVE2D_PET_SELFTEST_EXIT=1</c> 测完自动退出程序。
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        var lines = new List<string>();
        void Line(string text) => lines.Add(text);

        try
        {
            var wait = Stopwatch.StartNew();
            var framesAtStart = _renderedFrames;
            while (wait.ElapsedMilliseconds < 15000
                   && (_session.Runtime?.IsLoaded != true || _renderedFrames - framesAtStart < 3))
            {
                await Task.Delay(50);
            }

            var runtime = _session.Runtime;
            if (runtime?.IsLoaded != true)
            {
                Line("FAIL 模型未加载，跳过交互自测");
                return;
            }

            _pollTimer.Stop();   // 自测期间别让 40ms 轮询覆盖被测状态

            var cfg = Config;
            Line($"OK 窗口 {Width:0}×{Height:0} @({Left:0},{Top:0})，Topmost={Topmost}，Opacity={Opacity:0.00}，" +
                 $"已渲染 {_renderedFrames} 帧，画布比例 {_session.CanvasAspect:0.0000}");

            // ① 高宽按模型比例（宽度来自配置，高度 = 宽度 / 画布宽高比）
            var expectedHeight = Width / Math.Max(0.05, _session.CanvasAspect);
            Line(Math.Abs(Height - expectedHeight) < 1.5
                ? $"OK 高宽按模型比例：Width={Width:0} Height={Height:0}（期望 {expectedHeight:0.0}）"
                : $"FAIL 高宽按模型比例：Width={Width:0} Height={Height:0}（期望 {expectedHeight:0.0}）");

            // ② 拖动结束写回配置（模拟一次拖动 + 松手：窗口 +40,+30 → SavePlacement → 读回配置）
            var oldLeft = Left;
            var oldTop = Top;
            Left = oldLeft + 40;
            Top = oldTop + 30;
            ClampToVisibleArea();
            SavePlacement();
            var movedLeft = Left;
            var movedTop = Top;
            var positionSaved = cfg is not null
                                && cfg.PetLeft is { } savedLeft && cfg.PetTop is { } savedTop
                                && Math.Abs(savedLeft - movedLeft) < 0.01
                                && Math.Abs(savedTop - movedTop) < 0.01;
            Line(positionSaved
                ? $"OK 拖动结束写回配置：({oldLeft:0},{oldTop:0}) → ({movedLeft:0},{movedTop:0})，PetLeft={cfg!.PetLeft} PetTop={cfg.PetTop}"
                : $"FAIL 拖动结束写回配置：窗口 ({movedLeft:0},{movedTop:0})，PetLeft={cfg?.PetLeft} PetTop={cfg?.PetTop}");
            Left = oldLeft;
            Top = oldTop;
            ClampToVisibleArea();
            SavePlacement();

            // ③ 透明区域点击穿透：开 / 关 WS_EX_TRANSPARENT 并读回扩展样式位确认
            var hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough(true);
            var transparentSet = (GetExtendedStyle(hwnd) & WsExTransparent) != 0;
            ApplyClickThrough(false);
            var transparentCleared = (GetExtendedStyle(hwnd) & WsExTransparent) == 0;
            Line(transparentSet && transparentCleared
                ? "OK 点击穿透：WS_EX_TRANSPARENT 置位 / 清除都成功"
                : $"FAIL 点击穿透：置位={transparentSet} 清除={transparentCleared}");

            // ④ 全局鼠标眼控：取窗口内偏右的点换算画布坐标（不依赖窗口内鼠标），设目标后看运行时参数有没有真的变
            if (cfg?.EyeTracking == true)
            {
                var model = runtime.Model;
                var probeX = model.FindParameter("ParamAngleX") ?? model.FindParameter("AngleX");
                var probeEye = model.FindParameter("ParamEyeBallX");
                var baseX = probeX?.Value ?? 0f;
                var baseEye = probeEye?.Value ?? 0f;

                ComputeCanvasPoint(ActualWidth * 0.9, ActualHeight * 0.5, out var targetX, out var targetY);
                ClampCanvasTarget(ref targetX, ref targetY);
                _session.SetPetEyeTarget(targetX, targetY);
                _eyeTargetSet = true;

                await Task.Delay(900);
                var deltaX = Math.Abs((probeX?.Value ?? baseX) - baseX);
                var deltaEye = Math.Abs((probeEye?.Value ?? baseEye) - baseEye);
                Line(deltaX > 0.05f || deltaEye > 0.05f
                    ? $"OK 全局鼠标眼控：目标画布({targetX:0},{targetY:0}) → ΔParamAngleX={deltaX:0.000} ΔParamEyeBallX={deltaEye:0.000}"
                    : $"FAIL 全局鼠标眼控：目标画布({targetX:0},{targetY:0})，参数没变化（Δ={deltaX:0.000}/{deltaEye:0.000}）");
                _session.ClearPetEyeTarget();
                _eyeTargetSet = false;
            }
            else
            {
                Line("SKIP 全局鼠标眼控（配置里关闭了眼睛跟随）");
            }

            // ⑤ 眼控目标夹取：屏幕极远处的鼠标 → 画布目标应被夹在「画布外扩 30%」内
            var farX = (float)(runtime.Model.CanvasWidthPixels * 5);
            var farY = (float)(runtime.Model.CanvasHeightPixels * 5);
            ClampCanvasTarget(ref farX, ref farY);
            var clamped = farX <= runtime.Model.CanvasWidthPixels * 1.31f
                          && farY <= runtime.Model.CanvasHeightPixels * 1.31f;
            Line(clamped
                ? $"OK 眼控目标夹取：原始 5×画布 → 夹到 ({farX:0},{farY:0})"
                : $"FAIL 眼控目标夹取：({farX:0},{farY:0})");
        }
        catch (Exception ex)
        {
            Line("EXCEPTION " + ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            var report = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            try { _session.ViewModel?.Log("[桌宠自测] " + string.Join("；", lines)); } catch { /* 忽略 */ }
            try { _session.ViewModel?.Storage.WriteText("pet-selftest.log", report); } catch { /* 忽略 */ }
            _pollTimer.Start();

            if (Environment.GetEnvironmentVariable("YLT_LIVE2D_PET_SELFTEST_EXIT") is "1" or "on" or "true")
            {
                await Task.Delay(400);
                Application.Current?.Shutdown();
            }
        }
    }

    // ===================== 开发调试：截图 =====================

    /// <summary>等窗口渲染出第一帧后约 1.5 秒截图存盘，然后退出程序（<c>YLT_LIVE2D_SHOT_PET</c>）。</summary>
    private void ScheduleDebugCapture(string path)
    {
        if (_captureScheduled) return;
        _captureScheduled = true;

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            var watch = Stopwatch.StartNew();
            // 至少等 1.5 秒，同时确保已经渲染出至少一帧（模型同步加载时第一帧来得很快）
            while (watch.ElapsedMilliseconds < 8000
                   && (_renderedFrames == 0 || watch.ElapsedMilliseconds < 1500))
            {
                await Task.Delay(40);
            }

            try
            {
                CaptureToPng(path);
            }
            catch (Exception ex)
            {
                try { await File.WriteAllTextAsync(path + ".err.txt", ex.ToString()); } catch { /* 忽略 */ }
            }

            _shutdown = true;
            try { _pollTimer.Stop(); } catch { /* 忽略 */ }
            Application.Current?.Shutdown();
        }));
    }

    /// <summary>把窗口内容渲染成 PNG（透明底垫深色，方便一眼看出模型轮廓）。</summary>
    private void CaptureToPng(string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x18, 0x1C, 0x24)), null,
                new Rect(0, 0, width, height));
        }
        bitmap.Render(background);
        if (Content is Visual content) bitmap.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ===================== Win32 辅助 =====================

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
