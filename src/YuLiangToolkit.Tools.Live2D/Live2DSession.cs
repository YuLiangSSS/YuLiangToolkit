using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using YuLiangToolkit.Tools.Live2D.Interop;
using YuLiangToolkit.Tools.Live2D.Mocap;
using YuLiangToolkit.Tools.Live2D.Model;
using YuLiangToolkit.Tools.Live2D.Rendering;
using YuLiangToolkit.Tools.Live2D.ViewModels;
using YuLiangToolkit.Tools.Live2D.Views;

namespace YuLiangToolkit.Tools.Live2D;

/// <summary>
/// 页面 / 桌宠共用的一帧渲染目标：会话每帧先 <see cref="ModelRuntime.Tick"/> 一次，再依次叫它们各自出图。
///
/// <para>为什么要有这个接口：工具页和桌宠**同时可见**时都要拿到自己尺寸的画面。
/// 如果两边都往同一个 <c>renderer.Bitmap</c> 画，位图尺寸会被对方改来改去（还互相覆盖内容）。
/// 所以会话只 Tick 一次（动作速度才不会翻倍），两个目标各自用自己的 <see cref="CubismRenderer"/>
/// （共享同一份只读纹理）分别渲染。</para>
/// </summary>
public interface ILive2DFrameTarget
{
    /// <summary>这个目标现在值不值得渲染（页面切走 / 窗口最小化时返回 false，省 CPU）。</summary>
    bool IsTargetVisible { get; }

    /// <summary>用运行时当前这一帧画自己的位图（在 UI 线程、<see cref="ModelRuntime.Tick"/> 之后调用）。</summary>
    void RenderFrame(ModelRuntime runtime);
}

/// <summary>
/// 「Live2D 桌宠」的后台会话（整个进程一个）：管理模型运行时、唯一的帧循环、工具页与桌宠窗口的存活。
///
/// <para>与 KPS 的 Overlay 同一套思路：模型加载慢（5 张 4096 纹理）、桌宠窗口要在切到别的工具页之后
/// 继续显示，所以它们放在会话里；工具页只是「控制面板」，页面关掉时若桌宠还开着，会话继续跑。</para>
///
/// <para><b>帧循环</b>：会话持有一个 <see cref="DispatcherTimer"/>：每帧 = Tick 一次 → 页面目标渲染 → 桌宠目标渲染。
/// 页面和桌宠同时可见时桌宠降到约 15fps（页面仍按 <see cref="Live2DConfig.FpsLimit"/>），避免两张大图
/// 一起抢 UI 线程。页面切走 / 窗口最小化且桌宠也没开时定时器停下；两边都关掉后再过一会儿释放模型内存。</para>
///
/// <para><b>线程</b>：公开方法都应在 UI 线程调用；模型加载在后台线程（<see cref="Task.Run(Action)"/>），
/// 加载完成 / 失败都切回 UI 线程再改界面状态。开发调试截图模式（环境变量 <c>YLT_SHOT</c> /
/// <c>YLT_LIVE2D_SHOT_PET</c>）下改成 UI 线程同步加载，保证外壳 1200ms 后截图时画面已经出来。</para>
/// </summary>
public sealed class Live2DSession
{
    /// <summary>全局唯一会话。</summary>
    public static Live2DSession Current { get; } = new();

    /// <summary>两边都没人看着时，模型保留多久再释放内存（毫秒）。</summary>
    private const int IdleUnloadDelayMs = 45_000;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Live2DViewModel? _viewModel;
    private ModelRuntime? _runtime;
    private CubismRenderer? _petRenderer;
    private PetWindow? _pet;
    private ILive2DFrameTarget? _pageTarget;

    private DispatcherTimer? _frameTimer;
    private DispatcherTimer? _idleUnloadTimer;
    private TimeSpan _lastFrameTime;
    private long _frameIndex;

    private Task? _loadTask;
    private CancellationTokenSource? _loadCts;
    private double _lastReportedProgress = -1;
    private bool _loading;
    private double _loadProgress;
    private string? _loadError;
    private double _lastLoadMilliseconds;

    private bool _pageActive;
    private bool _initialized;
    private bool _shutdown;
    private bool _windowCloseHooked;
    private bool _frameErrorLogged;

    private DateTime _lastPageEyeTime = DateTime.MinValue;

    // 帧耗时测量：页面 / 桌宠各自上一帧的渲染毫秒数（页面与桌宠同开太吃力时给桌宠降频）
    private double _lastPageRenderMs;
    private double _lastPetRenderMs;
    private bool _dualSlowLogged;

    // ===== 摄像头面部动捕（数据源生命周期归会话管：页面 / 桌宠可见才开，隐私 + 省 CPU） =====
    private readonly SemaphoreSlim _mocapGate = new(1, 1);
    private MocapCameraSource? _mocap;
    private MocapSettings? _mocapAppliedSettings;
    private string? _mocapStartError;
    private string? _lastLoggedMocapError;
    private int _mocapRevision;
    private bool _mocapSelfTestStarted;

    // ===== 身体晃动演示钩子（YLT_LIVE2D_SWAY_DEMO） =====
    private bool _swayDemoApplied;

    // ===== 面部测试 / 鼠标跟随演示钩子（YLT_LIVE2D_FACE_DEMO / YLT_LIVE2D_EYE_DEMO，截图验收用） =====
    private bool _faceDemoApplied;
    private bool _eyeDemoApplied;

    private Live2DSession()
    {
    }

    // ===================== 对外状态 =====================

    /// <summary>工具页 ViewModel（没创建过为 null）。</summary>
    public Live2DViewModel? ViewModel => _viewModel;

    /// <summary>当前模型运行时（没加载 / 已释放为 null）。</summary>
    public ModelRuntime? Runtime => _runtime;

    /// <summary>桌宠专用的渲染器（与页面渲染器共享纹理；没加载 / 创建失败为 null）。</summary>
    public CubismRenderer? PetRenderer => _petRenderer;

    /// <summary>模型是否加载完成且可用。</summary>
    public bool IsLoaded => _runtime?.IsLoaded == true;

    /// <summary>是否正在加载模型。</summary>
    public bool IsLoading => _loading;

    /// <summary>加载进度 0~1。</summary>
    public double LoadProgress => _loadProgress;

    /// <summary>加载失败原因（成功为 null）。</summary>
    public string? LoadError => _loadError;

    /// <summary>最近一次加载耗时（毫秒，给界面的信息卡显示）。</summary>
    public double LastLoadMilliseconds => _lastLoadMilliseconds;

    /// <summary>工具页当前是否在前台（页面打开着）。</summary>
    public bool IsPageActive => _pageActive;

    /// <summary>桌宠窗口当前是否显示着。</summary>
    public bool IsPetVisible => _pet is not null;

    /// <summary>
    /// 是不是「开发调试截图模式」（设置了 <c>YLT_SHOT</c> 或 <c>YLT_LIVE2D_SHOT_PET</c>）：
    /// 这种模式下模型在 UI 线程同步加载、纹理固定 2048，保证外壳 1200ms 后截图时画面已经出来。
    /// </summary>
    public bool IsDebugShotMode =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_SHOT"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_LIVE2D_SHOT_PET"));

    /// <summary>桌宠截图钩子路径（环境变量 <c>YLT_LIVE2D_SHOT_PET</c>；没设置为空串）。</summary>
    public string PetDebugShotPath =>
        Environment.GetEnvironmentVariable("YLT_LIVE2D_SHOT_PET")?.Trim() ?? "";

    /// <summary>
    /// 画布宽高比（宽 / 高）。模型没加载时用内置模型的 3749 / 7697，保证桌宠窗口先有个合理的长宽比。
    /// </summary>
    public double CanvasAspect
    {
        get
        {
            var model = _runtime?.Model;
            try
            {
                if (model is not null && model.CanvasWidthPixels > 0 && model.CanvasHeightPixels > 0)
                    return model.CanvasWidthPixels / model.CanvasHeightPixels;
            }
            catch { /* 运行时没准备好时用默认比例 */ }
            return 3749.0 / 7697.0;
        }
    }

    /// <summary>加载状态 / 模型 / 桌宠显隐变化时触发（在 UI 线程）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>桌宠显示 / 隐藏时触发（在 UI 线程）。</summary>
    public event EventHandler? PetVisibilityChanged;

    // ===================== 动捕 / 身体晃动 对外状态 =====================

    /// <summary>当前动捕数据源（没创建为 null；UI 读 <c>Latest</c> / <c>LatestPreview</c> / <c>MeasuredFps</c>）。</summary>
    public MocapCameraSource? MocapSource => _mocap;

    /// <summary>动捕最近一次启动错误（空 = 没错误）。</summary>
    public string MocapStartError => _mocapStartError ?? "";

    /// <summary>动捕数据源是否正在采集。</summary>
    public bool IsMocapRunning => _mocap?.IsRunning == true;

    /// <summary>动捕数据源状态变化时触发（启动 / 停止 / 出错；可能在后台线程）。</summary>
    public event EventHandler? MocapStateChanged;

    /// <summary>身体晃动演示（「摇一摇」）还剩多少秒（0 = 没在演示）。</summary>
    public double SwayDemoRemainingSeconds
    {
        get
        {
            try { return _runtime?.SwayDemoRemainingSeconds ?? 0; } catch { return 0; }
        }
    }

    /// <summary>页面或桌宠现在是否有人看着（决定要不要开摄像头）。</summary>
    private bool IsPageOrPetVisible => _pageTarget is { IsTargetVisible: true } || _pet?.IsVisible == true;

    // ===================== 页面生命周期 =====================

    /// <summary>取（或创建）工具页 ViewModel（整个进程只建一次，页面重开复用）。</summary>
    public Live2DViewModel EnsureViewModel()
    {
        EnsureInitialized();
        if (_viewModel is null)
        {
            _viewModel = new Live2DViewModel();
            _viewModel.Session = this;
        }
        return _viewModel;
    }

    /// <summary>工具页面打开：恢复配置保存、按需加载模型 / 显示桌宠。</summary>
    public void Activate(Live2DViewModel viewModel)
    {
        if (_shutdown) return;
        EnsureInitialized();
        HookWindowClose();

        _viewModel = viewModel;
        viewModel.Session = this;
        _pageActive = true;
        try { viewModel.NotifyOpened(); } catch { /* 恢复自动保存失败不影响使用 */ }

        EnsureModelLoaded();
        ApplyRuntimeSettings();
        UpdateFrameLoop();
        ApplyMocapSettings();
        TryBeginSwayDemo();
        TryBeginFaceDemo();
        TryBeginEyeDemo();
        TryBeginMocapSelfTest();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>工具页面关闭（切走 / 回主页）；桌宠还开着就继续跑。</summary>
    public void Deactivate(Live2DViewModel? viewModel = null)
    {
        if (viewModel is not null && !ReferenceEquals(viewModel, _viewModel)) return;
        _pageActive = false;
        _pageTarget = null;
        UpdateFrameLoop();
        ApplyMocapSettings();   // 页面关掉且桌宠也没开 → 关摄像头

        // 页面关了、桌宠也没开：别让模型一直盯着一个不存在的鼠标
        if (_pet is null)
        {
            try { _runtime?.ClearEyeTrackingTarget(); } catch { /* 忽略 */ }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>程序退出：关桌宠、停帧循环、释放模型、把配置落盘。</summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;

        UnhookWindowClose();
        try { _loadCts?.Cancel(); } catch { /* 忽略 */ }
        _loadCts = null;
        _loadTask = null;
        _loading = false;

        try { _frameTimer?.Stop(); } catch { /* 忽略 */ }
        try { _idleUnloadTimer?.Stop(); } catch { /* 忽略 */ }
        _frameTimer = null;
        _idleUnloadTimer = null;

        ClosePetInternal(raiseEvent: false);
        DisposeMocapBlocking();
        DisposeRuntime();

        try { _viewModel?.ConfigStore.SaveNow(); } catch { /* 退出流程忽略 */ }
        try { _viewModel?.NotifyClosed(); } catch { /* 忽略 */ }
    }

    /// <summary>立刻保存配置（桌宠拖动 / 缩放 / 切换设置后调用，防抖窗口内的改动也不会丢）。</summary>
    public void SaveConfigNow()
    {
        try { _viewModel?.ConfigStore.SaveNow(); } catch { /* 忽略 */ }
    }

    // ===================== 模型加载 =====================

    /// <summary>
    /// 按需加载模型（已加载 / 正在加载时什么都不做）。
    /// <para>普通启动走后台线程（不卡界面）；开发调试截图模式（<see cref="IsDebugShotMode"/>）下
    /// 在 UI 线程同步加载，保证外壳截图时画面已经出来。</para>
    /// </summary>
    public void EnsureModelLoaded()
    {
        if (_shutdown) return;
        EnsureInitialized();
        HookWindowClose();
        if (_runtime is not null || _loadTask is not null || _loading) return;

        var vm = EnsureViewModel();
        _loading = true;
        _loadProgress = 0;
        _lastReportedProgress = -1;
        _loadError = null;
        _frameErrorLogged = false;
        vm.SetLoadStarted();
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (IsDebugShotMode)
        {
            // 截图模式：同步加载（UI 线程阻塞 1~3 秒，换来 1200ms 后截图里有最终画面）
            vm.Log("截图模式：同步加载模型…");
            DoLoad(vm, CancellationToken.None);
            return;
        }

        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        vm.Log("开始异步加载模型…");
        _loadTask = Task.Run(() => DoLoad(vm, token), token);
    }

    /// <summary>重新加载模型（换模型文件夹 / 改纹理尺寸后调用）。</summary>
    public void ReloadModel()
    {
        if (_shutdown) return;
        try { _loadCts?.Cancel(); } catch { /* 忽略 */ }
        _loadCts = null;
        _loadTask = null;
        _loading = false;
        _loadError = null;

        DisposeRuntime();
        _viewModel?.AttachRuntime(null, 0);
        _viewModel?.SetLoadStarted();
        _viewModel?.Log("重新加载模型…");
        EnsureModelLoaded();
    }

    /// <summary>真正做加载（后台线程 / 截图模式的 UI 线程都会走到这里）。</summary>
    private void DoLoad(Live2DViewModel vm, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (_shutdown) return;
            var cfg = vm.Cfg;
            var folder = string.IsNullOrWhiteSpace(cfg.ModelFolder)
                ? Live2DModelAsset.BuiltInModelDirectory
                : cfg.ModelFolder.Trim();
            var asset = Live2DModelAsset.Discover(folder);
            vm.Log($"模型资源：{asset.Name}（{asset.Directory}）");

            var textureSize = IsDebugShotMode ? 2048 : ClampTextureSize(cfg.TextureMaxSize);
            var options = new RuntimeLoadOptions
            {
                TextureMaxSize = textureSize,
                IdleMotionName = string.IsNullOrWhiteSpace(cfg.IdleMotion) ? null : cfg.IdleMotion.Trim(),
                Progress = progress => ReportLoadProgress(vm, progress),
                Log = message => vm.Log(message),
                CancellationToken = cancellationToken,
            };

            // Core 自己的日志也转进界面日志（加载失败时能看出 moc / 纹理哪一步出的问题）
            try { CubismCore.SetLogHandler(message => vm.Log("[Core] " + message)); } catch { /* 忽略 */ }
            ModelRuntime runtime;
            try
            {
                runtime = ModelRuntime.Load(asset, options);
            }
            finally
            {
                try { CubismCore.SetLogHandler(null); } catch { /* 忽略 */ }
            }

            watch.Stop();
            if (_shutdown || cancellationToken.IsCancellationRequested)
            {
                try { runtime.Dispose(); } catch { /* 忽略 */ }
                return;
            }

            vm.Ui(() => FinishLoad(vm, runtime, watch.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException)
        {
            // 页面 / 程序关闭时取消：什么都不用做
        }
        catch (Exception ex)
        {
            watch.Stop();
            vm.Ui(() => FailLoad(vm, ex));
        }
    }

    /// <summary>加载进度回调（可能来自后台线程；节流后切 UI 线程更新进度条）。</summary>
    private void ReportLoadProgress(Live2DViewModel vm, double progress)
    {
        if (!double.IsFinite(progress)) return;
        progress = Math.Clamp(progress, 0, 1);
        if (progress < 1 && progress - _lastReportedProgress < 0.01) return;
        _lastReportedProgress = progress;
        vm.Ui(() =>
        {
            _loadProgress = progress;
            vm.SetLoadProgress(progress);
        });
    }

    private static int ClampTextureSize(int value) => value switch
    {
        <= 1024 => 1024,
        <= 2048 => 2048,
        _ => 4096,
    };

    /// <summary>加载成功（UI 线程）：接上运行时、建桌宠渲染器、开始帧循环、渲染第一帧。</summary>
    private void FinishLoad(Live2DViewModel vm, ModelRuntime runtime, double loadMilliseconds)
    {
        if (_shutdown)
        {
            try { runtime.Dispose(); } catch { /* 忽略 */ }
            return;
        }

        _runtime = runtime;
        _lastLoadMilliseconds = loadMilliseconds;
        _loading = false;
        _loadProgress = 1;
        _loadError = null;
        _loadTask = null;
        _loadCts = null;

        // 桌宠用独立的渲染器（只共享只读纹理）：页面和桌宠同时显示时各画各的位图，互不覆盖
        _petRenderer = null;
        try
        {
            _petRenderer = new CubismRenderer(runtime.Renderer.Textures);
        }
        catch (Exception ex)
        {
            vm.Log("桌宠独立渲染器创建失败（桌宠将复用页面渲染器）：" + ex.Message);
        }

        vm.AttachRuntime(runtime, loadMilliseconds);
        ApplyRuntimeSettings();
        AssignMocapToRuntime();
        ApplyMocapSettings();
        vm.Log($"模型加载完成：{runtime.ModelName}，用时 {loadMilliseconds:0} ms" +
               $"（动作 {runtime.Motions.Count} 个，表情 {runtime.Expressions.Count} 个）");
        vm.SetStatus("模型已就绪");

        // 截图 / 验收用的演示钩子放在「模型已就绪」之后：状态栏能显示演示提示
        TryBeginSwayDemo();
        TryBeginFaceDemo();
        TryBeginEyeDemo();
        TryBeginMocapSelfTest();

        UpdateFrameLoop();
        RenderFirstFrame();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>加载失败（UI 线程）：界面显示错误 + 日志，桌宠窗口保持透明（不会崩）。</summary>
    private void FailLoad(Live2DViewModel vm, Exception exception)
    {
        _loading = false;
        _loadProgress = 0;
        _loadTask = null;
        _loadCts = null;
        _loadError = exception.Message;
        DisposeRuntime();

        var message = "模型加载失败：" + exception.Message;
        vm.Log(message);
        try { vm.Log(exception.ToString()); } catch { /* 忽略 */ }
        vm.SetLoadFailed(exception.Message);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>释放当前模型（页面 / 桌宠都不用了、重新加载前、程序退出时）。</summary>
    private void DisposeRuntime()
    {
        var runtime = _runtime;
        _runtime = null;
        var petRenderer = _petRenderer;
        _petRenderer = null;

        try { petRenderer?.Dispose(); } catch { /* 忽略 */ }
        try { runtime?.Dispose(); } catch { /* 忽略 */ }

        _frameIndex = 0;
    }

    /// <summary>加载完成后立刻出一帧（截图模式要保证 Bitmap 已生成，不能等定时器）。</summary>
    private void RenderFirstFrame()
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;

        try { runtime.Tick(TimeSpan.FromMilliseconds(16)); } catch { /* 一帧失败不影响后面 */ }

        var page = _pageTarget;
        if (page is { IsTargetVisible: true })
        {
            try { page.RenderFrame(runtime); } catch { /* 渲染异常由帧循环统一处理 */ }
        }
        if (_pet is { IsVisible: true } pet)
        {
            try { pet.RenderFrame(runtime); } catch { /* 同上 */ }
        }
        try { _viewModel?.RefreshFrameState(runtime); } catch { /* 忽略 */ }
    }

    // ===================== 设置 / 帧率 =====================

    /// <summary>把配置里的运行时开关（物理 / 待机 / 眨眼 / 眼控强度 / 待机动作）推给模型运行时。</summary>
    public void ApplyRuntimeSettings()
    {
        var runtime = _runtime;
        var cfg = _viewModel?.Cfg;
        if (runtime is null || cfg is null) return;

        try
        {
            runtime.PhysicsEnabled = cfg.PhysicsEnabled;
            runtime.PhysicsStrength = Math.Clamp(cfg.PhysicsStrength, 0, 1);
            runtime.IdleEnabled = cfg.IdleEnabled;
            runtime.AutoBlink = cfg.AutoBlink;
            runtime.EyeTrackingEnabled = cfg.EyeTracking;
            runtime.EyeTrackingSmoothing = Math.Clamp(cfg.EyeTrackingSmoothing, 0.05, 1);
            runtime.EyeTrackingHeadStrength = Math.Clamp(cfg.EyeTrackingHeadStrength, 0.2, 1.5);
            runtime.EyeTrackingEyeStrength = Math.Clamp(cfg.EyeTrackingEyeStrength, 0.2, 1.5);
            runtime.EyeTrackingBodyStrength = Math.Clamp(cfg.EyeTrackingBodyStrength, 0, 1.5);

            // 鼠标跟随灵敏度：横向 / 抬头 / 低头 / 纵向偏移（桌宠全局鼠标也共用这套）
            runtime.EyeTrackingHorizontalGain = Math.Clamp(cfg.EyeTrackingHorizontalGain, 0.3, 3);
            runtime.EyeTrackingUpGain = Math.Clamp(cfg.EyeTrackingUpGain, 0.3, 3);
            runtime.EyeTrackingDownGain = Math.Clamp(cfg.EyeTrackingDownGain, 0.3, 3);
            runtime.EyeTrackingVerticalOffset = Math.Clamp(cfg.EyeTrackingVerticalOffset, -0.5, 0.5);

            runtime.IdleMotion = FindIdleMotion(runtime, cfg.IdleMotion);

            // 摄像头面部动捕：驱动开关与强度（数据源本身由 ApplyMocapSettings 启停 / 重建）
            runtime.MocapEnabled = cfg.MocapEnabled;
            runtime.MocapHeadEnabled = cfg.MocapHeadEnabled;
            runtime.MocapBlinkEnabled = cfg.MocapBlinkEnabled;
            runtime.MocapMouthEnabled = cfg.MocapMouthEnabled;
            runtime.MocapHeadStrength = Math.Clamp(cfg.MocapHeadStrength, 0, 1.5);
            runtime.MocapEyeStrength = Math.Clamp(cfg.MocapEyeStrength, 0, 1.5);
            runtime.MocapMouthStrength = Math.Clamp(cfg.MocapMouthStrength, 0, 1.5);

            // 身体晃动测试（参数由运行时 BodySwayController 每帧写；「摇一摇」是临时演示）
            runtime.SwayEnabled = cfg.BodySwayEnabled;
            runtime.SwayMode = cfg.BodySwayMode;
            runtime.SwayAmplitudeDegrees = Math.Clamp(cfg.BodySwayAmplitude, 0, 30);
            runtime.SwaySpeedHz = Math.Clamp(cfg.BodySwaySpeed, 0.1, 2);
            runtime.SwayHeadFollow = Math.Clamp(cfg.BodySwayHeadFollow, 0, 1);
            runtime.SwayIncludeHips = cfg.BodySwayIncludeHips;
        }
        catch (Exception ex)
        {
            _viewModel?.Log("应用运行时设置失败：" + ex.Message);
        }

        ApplyFrameRate();
    }

    /// <summary>按配置里的待机动作名找 MotionEntry（空 / 找不到返回 null = 让运行时自己挑）。</summary>
    private static MotionEntry? FindIdleMotion(ModelRuntime runtime, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var motion in runtime.Motions)
        {
            if (string.Equals(motion.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return motion;
        }
        return null;
    }

    /// <summary>帧率上限改了之后重设定时器间隔。</summary>
    public void ApplyFrameRate()
    {
        if (_frameTimer is null) return;
        var fps = Math.Clamp(_viewModel?.Cfg.FpsLimit ?? 30, 15, 60);
        var interval = TimeSpan.FromSeconds(1.0 / fps);
        if (Math.Abs((_frameTimer.Interval - interval).TotalMilliseconds) > 0.5) _frameTimer.Interval = interval;
    }

    // ===================== 帧循环 =====================

    /// <summary>页面 / 桌宠可见性或加载状态变化后调它：决定帧循环开还是关。</summary>
    public void RefreshFrameLoop()
    {
        UpdateFrameLoop();
        ApplyMocapSettings();   // 窗口最小化 / 还原会影响「页面可见」→ 摄像头跟着开 / 关
    }

    /// <summary>页面目标挂上来（工具页 Loaded）。</summary>
    public void AttachPageTarget(ILive2DFrameTarget target)
    {
        if (_shutdown) return;
        EnsureInitialized();
        HookWindowClose();
        _pageTarget = target;
        UpdateFrameLoop();
        ApplyMocapSettings();
        TryBeginMocapSelfTest();
        if (IsLoaded)
        {
            try { _runtime!.Tick(TimeSpan.FromMilliseconds(16)); } catch { /* 忽略 */ }
            try { target.RenderFrame(_runtime!); } catch { /* 忽略 */ }
        }
    }

    /// <summary>页面目标摘下来（工具页 Unloaded）。</summary>
    public void DetachPageTarget(ILive2DFrameTarget target)
    {
        if (ReferenceEquals(_pageTarget, target)) _pageTarget = null;
        UpdateFrameLoop();
        ApplyMocapSettings();
    }

    private void UpdateFrameLoop()
    {
        if (_shutdown) return;

        var pageVisible = _pageTarget is { IsTargetVisible: true };
        var petVisible = _pet?.IsVisible == true;
        var need = _runtime?.IsLoaded == true && (pageVisible || petVisible);

        if (need)
        {
            if (_frameTimer is null)
            {
                _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(_viewModel?.Cfg.FpsLimit ?? 30, 15, 60)),
                };
                _frameTimer.Tick += OnFrameTick;
            }
            if (!_frameTimer.IsEnabled)
            {
                _lastFrameTime = _clock.Elapsed;
                _frameTimer.Start();
            }
        }
        else
        {
            _frameTimer?.Stop();
        }

        ScheduleIdleUnload(pageTargetAttached: _pageTarget is not null, petVisible: _pet is not null);
    }

    /// <summary>页面 / 桌宠都没了：过一会儿释放模型内存（避免在别的工具页呆着白占 ~100MB）。</summary>
    private void ScheduleIdleUnload(bool pageTargetAttached, bool petVisible)
    {
        if (_runtime is null)
        {
            _idleUnloadTimer?.Stop();
            return;
        }

        if (pageTargetAttached || petVisible)
        {
            _idleUnloadTimer?.Stop();
            return;
        }

        if (_idleUnloadTimer is null)
        {
            _idleUnloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IdleUnloadDelayMs) };
            _idleUnloadTimer.Tick += OnIdleUnloadTick;
        }
        _idleUnloadTimer.Stop();
        _idleUnloadTimer.Start();
    }

    private void OnIdleUnloadTick(object? sender, EventArgs e)
    {
        _idleUnloadTimer?.Stop();
        if (_pageTarget is not null || _pet is not null) return;
        _viewModel?.Log("页面与桌宠都已关闭，释放模型内存（再次打开会重新加载）");
        _viewModel?.SetStatus("模型未加载：点「重新加载模型」或重新打开桌宠");
        DisposeRuntime();
        _viewModel?.AttachRuntime(null, 0);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>每帧：Tick 一次 → 页面渲染 → 桌宠渲染（两边都可见时桌宠降到约 15fps）。</summary>
    private void OnFrameTick(object? sender, EventArgs e)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded)
        {
            _frameTimer?.Stop();
            return;
        }

        var now = _clock.Elapsed;
        var delta = now - _lastFrameTime;
        _lastFrameTime = now;
        if (delta <= TimeSpan.Zero) return;
        // 卡顿 / 系统休眠后别把动作一次快进太多
        if (delta > TimeSpan.FromMilliseconds(200)) delta = TimeSpan.FromMilliseconds(100);

        try
        {
            runtime.Tick(delta);
        }
        catch (Exception ex)
        {
            HandleFrameException(ex);
            return;
        }

        _frameIndex++;

        var page = _pageTarget;
        var pageVisible = page is { IsTargetVisible: true };
        if (pageVisible)
        {
            var pageWatch = Stopwatch.StartNew();
            try { page!.RenderFrame(runtime); }
            catch (Exception ex) { HandleFrameException(ex); return; }
            _lastPageRenderMs = pageWatch.Elapsed.TotalMilliseconds;
        }

        if (_pet is { IsVisible: true } pet)
        {
            var fpsLimit = Math.Clamp(_viewModel?.Cfg.FpsLimit ?? 30, 15, 60);
            int petSkip;
            if (!pageVisible)
            {
                petSkip = 1;   // 只有桌宠：每帧都画（窗口小）
            }
            else
            {
                // 页面 + 桌宠同时可见：两张大图各渲染一次。实测明显吃力时把桌宠降到约 10fps（页面不受影响），
                // 并在日志 / 状态栏说明一次，免得用户以为掉帧是 bug。
                var heavy = _lastPageRenderMs + _lastPetRenderMs > 25;
                if (heavy && !_dualSlowLogged)
                {
                    _dualSlowLogged = true;
                    var message = $"页面与桌宠同时显示，每帧渲染约 {_lastPageRenderMs + _lastPetRenderMs:0} ms，" +
                                  "桌宠画面自动降到约 10fps（测试页仍按设定帧率）";
                    _viewModel?.Log(message);
                    _viewModel?.SetStatus(message);
                }
                petSkip = heavy
                    ? Math.Max(3, (int)Math.Round(fpsLimit / 10.0))
                    : Math.Max(2, (int)Math.Round(fpsLimit / 15.0));
            }

            if (_frameIndex % petSkip == 0)
            {
                var petWatch = Stopwatch.StartNew();
                try { pet.RenderFrame(runtime); }
                catch (Exception ex) { HandleFrameException(ex); return; }
                _lastPetRenderMs = petWatch.Elapsed.TotalMilliseconds;
            }
        }
        else
        {
            _lastPetRenderMs = 0;
        }

        // 界面上的动作进度 / 参数状态不用每帧刷，每 6 帧刷一次就够（避免无谓的绑定开销）
        if (_frameIndex % 6 == 0)
        {
            try { _viewModel?.RefreshFrameState(runtime); } catch { /* 忽略 */ }
        }
    }

    /// <summary>帧循环里出错：只记一次并停下，避免每帧刷日志把程序拖死。</summary>
    private void HandleFrameException(Exception exception)
    {
        if (_frameErrorLogged) return;
        _frameErrorLogged = true;
        try { _frameTimer?.Stop(); } catch { /* 忽略 */ }
        _viewModel?.Log("渲染帧出错，已暂停渲染：" + exception.Message);
        _viewModel?.SetStatus("渲染出错：" + exception.Message);
    }

    // ===================== 动作 / 表情 / 参数控制 =====================

    /// <summary>播放动作（loop = null 时用动作自带的循环建议；fadeSeconds = null 时用配置里的淡入淡出时长）。</summary>
    public void PlayMotion(MotionEntry motion, bool? loop = null, double? fadeSeconds = null)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;
        var fade = Math.Clamp(fadeSeconds ?? _viewModel?.Cfg.MotionFadeSeconds ?? 0.3, 0, 2);
        try
        {
            runtime.PlayMotion(motion, loop, fade);
            _viewModel?.Log($"播放动作：{motion.Name}（{motion.Duration:0.0}s，{(loop ?? motion.Loop ? "循环" : "单次")}，淡入 {fade:0.00}s）");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("播放动作失败：" + ex.Message);
        }
    }

    /// <summary>停止当前动作。</summary>
    public void StopMotion()
    {
        try
        {
            _runtime?.StopMotion();
            _viewModel?.Log("已停止动作");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("停止动作失败：" + ex.Message);
        }
    }

    /// <summary>施加表情（多个表情可叠加；淡入时长取配置）。</summary>
    public void ApplyExpression(ExpressionEntry expression, double fadeSeconds)
    {
        try { _runtime?.ApplyExpression(expression, Math.Max(0, fadeSeconds)); }
        catch (Exception ex) { _viewModel?.Log("施加表情失败：" + ex.Message); }
    }

    /// <summary>清除全部表情（淡出时长取配置）。</summary>
    public void ClearExpressions(double fadeSeconds)
    {
        try { _runtime?.ClearExpressions(Math.Max(0, fadeSeconds)); }
        catch (Exception ex) { _viewModel?.Log("清除表情失败：" + ex.Message); }
    }

    /// <summary>把所有参数 / 部件恢复默认（同时停掉动作与表情）。</summary>
    public void ResetAll()
    {
        try
        {
            _runtime?.ResetParameters();
            _viewModel?.Log("已重置全部参数与部件");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("重置失败：" + ex.Message);
        }
    }

    // ===================== 眼睛跟随 =====================

    /// <summary>页面预览里的鼠标位置（画布像素坐标）：页面活着时由页面优先控制眼神。</summary>
    public void SetPageEyeTarget(float canvasX, float canvasY)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;
        _lastPageEyeTime = DateTime.UtcNow;
        try { runtime.SetEyeTrackingTarget(canvasX, canvasY); } catch { /* 忽略 */ }
    }

    /// <summary>鼠标离开预览区：交回给桌宠（桌宠没开就是回正）。</summary>
    public void ClearPageEyeTarget()
    {
        _lastPageEyeTime = DateTime.MinValue;
        try { _runtime?.ClearEyeTrackingTarget(); } catch { /* 忽略 */ }
    }

    /// <summary>页面最近是否还在控制眼神（桌宠据此决定要不要抢）。</summary>
    public bool PageOwnsEyeTracking => (DateTime.UtcNow - _lastPageEyeTime).TotalMilliseconds < 400;

    /// <summary>桌宠用全局鼠标位置控制眼神（画布像素坐标）。</summary>
    public void SetPetEyeTarget(float canvasX, float canvasY)
    {
        if (PageOwnsEyeTracking) return;
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;
        try { runtime.SetEyeTrackingTarget(canvasX, canvasY); } catch { /* 忽略 */ }
    }

    /// <summary>桌宠不再控制眼神（眼控关掉 / 鼠标取不到 / 桌宠关闭）：回正，别留旧目标。</summary>
    public void ClearPetEyeTarget()
    {
        if (PageOwnsEyeTracking) return;   // 页面正在控制时别抢
        try { _runtime?.ClearEyeTrackingTarget(); } catch { /* 忽略 */ }
    }

    // ===================== 摄像头面部动捕 =====================

    /// <summary>
    /// 按配置让动捕数据源对齐目标状态：该开就开、该关就关、设置变了就整源重建。
    /// <para>可快速连续调用：内部按 revision + 信号量串行，只有最后一次请求真正落地（避免连点开关时
    /// 反复开关摄像头）。启动 / 停止 / 重建都在 UI 线程上下文继续执行，失败只记 <see cref="MocapStartError"/>
    /// 和日志，不会让程序崩。</para>
    /// </summary>
    public void ApplyMocapSettings()
    {
        if (_shutdown) return;
        _ = ReconcileMocapAsync();
    }

    private async Task ReconcileMocapAsync()
    {
        var revision = Interlocked.Increment(ref _mocapRevision);
        try { await _mocapGate.WaitAsync(); }
        catch (ObjectDisposedException) { return; }

        try
        {
            if (_shutdown || revision != Volatile.Read(ref _mocapRevision)) return;

            var cfg = _viewModel?.Cfg;
            if (cfg is null)
            {
                await StopMocapAsync();
                AssignMocapToRuntime();
                RaiseMocapStateChanged();
                return;
            }

            // 截图 / 桌宠截图模式绝不开摄像头（隐私：截图流程里不弹摄像头权限、不采集画面）
            var wantRunning = cfg.MocapEnabled && IsPageOrPetVisible && !IsDebugShotMode;
            var settings = MocapSettings.FromConfig(cfg);

            // 设置变了（摄像头 / 镜像 / 帧率 / 平滑 / 预览 / 中性位校准）：旧源释放，按新设置重建
            if (_mocap is not null && !MocapSettingsEqual(_mocapAppliedSettings, settings))
            {
                await DisposeMocapAsync();
            }

            if (!wantRunning)
            {
                await StopMocapAsync();
                RaiseMocapStateChanged();
                return;
            }

            if (_mocap is null)
            {
                _mocap = new MocapCameraSource(settings);
                _mocapAppliedSettings = settings;
                _mocap.StateChanged += OnMocapStateChanged;
                _mocapStartError = null;
                _lastLoggedMocapError = null;
                AssignMocapToRuntime();
                _viewModel?.Log($"动捕：准备打开摄像头（{DescribeMocapCamera(settings)}）");
            }

            if (revision != Volatile.Read(ref _mocapRevision))
            {
                RaiseMocapStateChanged();
                return;   // 排队期间配置又变了：交给最后一次
            }

            if (!_mocap.IsRunning)
            {
                var watch = Stopwatch.StartNew();
                var started = await _mocap.StartAsync();
                watch.Stop();
                if (revision != Volatile.Read(ref _mocapRevision))
                {
                    RaiseMocapStateChanged();
                    return;
                }

                if (started)
                {
                    _mocapStartError = null;
                    var frame = _mocap.FrameSize;
                    _viewModel?.Log($"动捕已启动：{DescribeMocapCamera(settings)}（{settings.Fps} fps 上限，" +
                                    $"预览{(settings.EnablePreview ? "开" : "关")}，画面 {frame.Width}×{frame.Height}，" +
                                    $"启动耗时 {watch.ElapsedMilliseconds} ms）");
                }
                else
                {
                    _mocapStartError = _mocap.LastError ?? "启动失败（没有可用摄像头 / 权限被拒 / 被其它程序占用）";
                    _viewModel?.Log("动捕启动失败：" + _mocapStartError);
                }
            }

            AssignMocapToRuntime();
            RaiseMocapStateChanged();
        }
        catch (Exception ex)
        {
            _mocapStartError = ex.Message;
            _viewModel?.Log("动捕异常：" + ex.Message);
            RaiseMocapStateChanged();
        }
        finally
        {
            try { _mocapGate.Release(); } catch { /* 已释放 */ }
        }
    }

    /// <summary>把数据源交给模型运行时（运行时只依赖 IMocapProvider 接口读结果）。</summary>
    private void AssignMocapToRuntime()
    {
        var runtime = _runtime;
        if (runtime is null) return;
        try { runtime.Mocap = _mocap; } catch { /* 模型还没加载完 */ }
    }

    /// <summary>停止采集（窗口关掉 / 动捕关掉时；数据源保留，下次可快速重启）。</summary>
    private async Task StopMocapAsync()
    {
        var source = _mocap;
        if (source is null) return;
        if (!source.IsRunning) { AssignMocapToRuntime(); return; }

        try { await source.StopAsync(); }
        catch (Exception ex) { _viewModel?.Log("动捕停止失败：" + ex.Message); }
        AssignMocapToRuntime();
    }

    /// <summary>释放当前数据源（换摄像头 / 换设置时；之后按需重建）。</summary>
    private async Task DisposeMocapAsync()
    {
        var source = _mocap;
        _mocap = null;
        _mocapAppliedSettings = null;
        if (source is null) return;

        try { source.StateChanged -= OnMocapStateChanged; } catch { /* 忽略 */ }
        try { await source.StopAsync(); } catch { /* 忽略 */ }
        try { source.Dispose(); } catch { /* 忽略 */ }
        AssignMocapToRuntime();
    }

    /// <summary>程序退出 / 主窗口关闭时的同步释放（不能再 await）。</summary>
    private void DisposeMocapBlocking()
    {
        var source = _mocap;
        _mocap = null;
        _mocapAppliedSettings = null;
        if (source is null) return;

        try { source.StateChanged -= OnMocapStateChanged; } catch { /* 忽略 */ }
        try { source.Dispose(); } catch { /* 忽略 */ }   // Dispose 内部会同步 Stop
        AssignMocapToRuntime();
    }

    private void OnMocapStateChanged(object? sender, EventArgs e)
    {
        var error = _mocap?.LastError;
        if (!string.IsNullOrEmpty(error) && !string.Equals(error, _lastLoggedMocapError, StringComparison.Ordinal))
        {
            _lastLoggedMocapError = error;
            _viewModel?.Log("动捕状态：" + error);
        }
        _viewModel?.Ui(RaiseMocapStateChanged);
    }

    private void RaiseMocapStateChanged()
    {
        try { MocapStateChanged?.Invoke(this, EventArgs.Empty); } catch { /* 订阅方异常不影响采集 */ }
    }

    private static bool MocapSettingsEqual(MocapSettings? left, MocapSettings? right)
    {
        if (left is null || right is null) return ReferenceEquals(left, right);
        return string.Equals(left.CameraId, right.CameraId, StringComparison.Ordinal)
               && string.Equals(left.CameraName, right.CameraName, StringComparison.Ordinal)
               && left.Mirror == right.Mirror
               && left.Fps == right.Fps
               && left.EnablePreview == right.EnablePreview
               && Math.Abs(left.Smoothing - right.Smoothing) < 0.0001
               && Math.Abs(left.NeutralFaceX - right.NeutralFaceX) < 0.0001
               && Math.Abs(left.NeutralFaceY - right.NeutralFaceY) < 0.0001;
    }

    private static string DescribeMocapCamera(MocapSettings settings)
        => string.IsNullOrWhiteSpace(settings.CameraName)
            ? (string.IsNullOrWhiteSpace(settings.CameraId) ? "自动选择（第一个摄像头）" : settings.CameraId)
            : settings.CameraName;

    /// <summary>重新校准表情（眨眼 / 张嘴的自动基准清零；换环境 / 光线后调）。</summary>
    public void ResetMocapCalibration()
    {
        try
        {
            _mocap?.ResetCalibration();
        }
        catch (Exception ex)
        {
            _viewModel?.Log("重新校准表情失败：" + ex.Message);
            return;
        }

        _viewModel?.Log("动捕：已重新校准表情（眨眼 / 张嘴的自动基准清零）");
        _viewModel?.SetStatus("动捕：表情基准已重新校准");
    }

    /// <summary>
    /// 校准中性位：把当前「有脸」时的脸中心位置写成新的 0 度参考
    /// （换座位 / 换摄像头后「正前方」不对时用）。
    /// </summary>
    public void CalibrateMocapNeutralPosition()
    {
        var cfg = _viewModel?.Cfg;
        if (cfg is null) return;

        var latest = _mocap?.Latest;
        if (latest is not { HasFace: true })
        {
            _viewModel?.Log("校准中性位失败：动捕还没检测到人脸（先把脸放到画面中央、保持正面朝前）");
            _viewModel?.SetStatus("校准中性位：还没检测到人脸");
            return;
        }

        cfg.MocapNeutralFaceX = Math.Clamp(latest.FaceX, 0.05, 0.95);
        cfg.MocapNeutralFaceY = Math.Clamp(latest.FaceY, 0.05, 0.95);
        _viewModel?.Log($"动捕：中性位已校准到画面 ({cfg.MocapNeutralFaceX:0.000}, {cfg.MocapNeutralFaceY:0.000})" +
                        "，数据源会按新基准重建");
        _viewModel?.SetStatus("动捕：中性位已校准");
        ApplyMocapSettings();   // 中性位变了 → 重建数据源
    }

    // ===================== 身体晃动测试 =====================

    /// <summary>「摇一摇」：让运行时按当前模式 / 幅度 / 速度临时晃 N 秒（默认 3 秒）。</summary>
    public void StartSwayDemo(double seconds = 3.0)
    {
        var runtime = _runtime;
        if (runtime is null || !runtime.IsLoaded) return;

        try
        {
            runtime.StartSwayDemo(Math.Clamp(seconds, 0.5, 60));
            _viewModel?.Log($"身体晃动：摇一摇 {seconds:0.0} 秒（{_viewModel?.Cfg.BodySwayMode ?? "左右摇"}）");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("摇一摇失败：" + ex.Message);
        }
    }

    /// <summary>停止「摇一摇」演示（不动「开启身体晃动」配置）。</summary>
    public void StopSwayDemo()
    {
        try { _runtime?.StopSwayDemo(); } catch { /* 忽略 */ }
    }

    /// <summary>开发调试（<c>YLT_LIVE2D_SWAY_DEMO=1</c>）：打开页面自动左右摇 15° / 0.5Hz 并演示 10 秒。</summary>
    private void TryBeginSwayDemo()
    {
        if (_swayDemoApplied) return;
        if (Environment.GetEnvironmentVariable("YLT_LIVE2D_SWAY_DEMO") is not ("1" or "on" or "true")) return;
        if (_viewModel is null || _runtime?.IsLoaded != true) return;

        _swayDemoApplied = true;
        var cfg = _viewModel.Cfg;
        cfg.BodySwayEnabled = true;
        cfg.BodySwayMode = "左右摇";
        cfg.BodySwayAmplitude = 15;
        cfg.BodySwaySpeed = 0.5;
        cfg.BodySwayHeadFollow = 0.35;
        ApplyRuntimeSettings();
        try { _runtime.StartSwayDemo(10); } catch { /* 忽略 */ }
        _viewModel.RefreshAllBindings();
        _viewModel.SetStatus("截图模式：身体晃动演示 10 秒（左右摇 15° / 0.5Hz）");
        _viewModel.Log("截图模式（YLT_LIVE2D_SWAY_DEMO）：已启用身体晃动演示 10 秒（左右摇 15° / 0.5Hz）");
    }

    // ===================== 开发调试：面部测试 / 鼠标跟随演示 =====================

    /// <summary>
    /// 开发调试（<c>YLT_LIVE2D_FACE_DEMO</c>）：打开页面自动应用一个「面部测试」预设，
    /// 方便截图里直接看到吐舌头等效果。值可以是预设名（tongue/open/pucker/smile/wink/rolleyes/reset）
    /// 或中文（吐舌头/张嘴/撅嘴/微笑/眨眼/翻白眼/重置面部），<c>1</c> = 吐舌头。
    /// 同时关掉眼睛跟随 / 自动眨眼 / 动捕，避免它们每帧把这些参数覆盖掉。
    /// </summary>
    private void TryBeginFaceDemo()
    {
        if (_faceDemoApplied) return;
        var raw = Environment.GetEnvironmentVariable("YLT_LIVE2D_FACE_DEMO")?.Trim();
        if (string.IsNullOrEmpty(raw)) return;
        if (_viewModel is null || _runtime?.IsLoaded != true) return;

        _faceDemoApplied = true;
        var preset = raw.ToLowerInvariant() switch
        {
            "1" or "on" or "true" => "tongue",
            "吐舌头" => "tongue",
            "张嘴" => "open",
            "撅嘴" => "pucker",
            "微笑" => "smile",
            "眨眼" => "wink",
            "翻白眼" => "rolleyes",
            "重置面部" => "reset",
            _ => raw.ToLowerInvariant(),
        };

        var cfg = _viewModel.Cfg;
        cfg.EyeTracking = false;    // 手动面部值不被眼睛跟随每帧覆盖
        cfg.AutoBlink = false;      // 不被自动眨眼覆盖
        cfg.MocapEnabled = false;   // 动捕也先关（截图验收不碰摄像头）
        ApplyRuntimeSettings();
        ApplyMocapSettings();
        _viewModel.RefreshAllBindings();
        _viewModel.ApplyFacePreset(preset);
        _viewModel.RefreshAllBindings();
        _viewModel.SetStatus("截图模式：已应用面部预设 " + Live2DViewModel.FacePresetDisplayName(preset));
        _viewModel.Log("截图模式（YLT_LIVE2D_FACE_DEMO）：已关闭眼控/自动眨眼/动捕，并应用面部预设 " + preset);
        _ = ReportFaceDemoResultAsync(preset);
    }

    /// <summary>面部演示：0.8 秒后把「实际参数值」写 Data\live2d\face-demo.log（验收证据，便于判断哪些值被运行时覆盖）。</summary>
    private async Task ReportFaceDemoResultAsync(string preset)
    {
        await Task.Delay(800);

        var viewModel = _viewModel;
        if (viewModel is null) return;
        viewModel.Ui(() =>
        {
            try
            {
                var text =
                    $"面部预设：{preset}（{Live2DViewModel.FacePresetDisplayName(preset)}）" + Environment.NewLine +
                    $"关闭的接管：EyeTracking={viewModel.Cfg.EyeTracking}，AutoBlink={viewModel.Cfg.AutoBlink}，" +
                    $"MocapEnabled={viewModel.Cfg.MocapEnabled}" + Environment.NewLine + Environment.NewLine +
                    "800ms 后从模型读回的实际参数值：" + Environment.NewLine +
                    viewModel.BuildFaceValuesSnapshot();
                viewModel.Storage.WriteText("face-demo.log", text);
                viewModel.Log("截图模式：面部预设的实际参数值已写入 Data\\live2d\\face-demo.log");
            }
            catch (Exception ex)
            {
                viewModel.Log("面部演示证据写入失败：" + ex.Message);
            }
        });
    }

    /// <summary>
    /// 开发调试（<c>YLT_LIVE2D_EYE_DEMO=down|up|left|right</c>）：把眼睛跟随目标设到画布外对应方向，
    /// 0.8 秒后把 ParamEyeBallX/Y、ParamAngleX/Y 写进日志，用来验证「低头灵敏度」等滑杆链路。
    /// </summary>
    private void TryBeginEyeDemo()
    {
        if (_eyeDemoApplied) return;
        var raw = Environment.GetEnvironmentVariable("YLT_LIVE2D_EYE_DEMO")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw)) return;
        if (_viewModel is null || _runtime?.IsLoaded != true) return;

        _eyeDemoApplied = true;
        try
        {
            var model = _runtime.Model;
            var canvasWidth = (float)Math.Max(1.0, model.CanvasWidthPixels);
            var canvasHeight = (float)Math.Max(1.0, model.CanvasHeightPixels);
            var (canvasX, canvasY) = raw switch
            {
                "up" => (canvasWidth * 0.5f, -canvasHeight * 0.6f),
                "left" => (-canvasWidth * 0.6f, canvasHeight * 0.5f),
                "right" => (canvasWidth * 1.6f, canvasHeight * 0.5f),
                _ => (canvasWidth * 0.5f, canvasHeight * 1.6f),   // down（默认）
            };

            var cfg = _viewModel.Cfg;
            cfg.EyeTracking = true;
            ApplyRuntimeSettings();
            _viewModel.RefreshAllBindings();
            _runtime.SetEyeTrackingTarget(canvasX, canvasY);
            _viewModel.Log($"截图模式（YLT_LIVE2D_EYE_DEMO）：眼睛跟随目标设为画布 ({canvasX:0},{canvasY:0})（{raw}）");
            _ = ReportEyeDemoResultAsync(raw);
        }
        catch (Exception ex)
        {
            _viewModel.Log("眼控演示失败：" + ex.Message);
        }
    }

    private async Task ReportEyeDemoResultAsync(string direction)
    {
        await Task.Delay(800);

        var viewModel = _viewModel;
        if (viewModel is null) return;
        viewModel.Ui(() =>
        {
            try
            {
                var model = _runtime?.Model;
                if (model is null) return;
                double Value(string id) => model.FindParameter(id)?.Value ?? 0;
                var text =
                    $"眼睛跟随目标方向：{direction}" + Environment.NewLine +
                    $"ParamEyeBallX={Value("ParamEyeBallX"):0.000}  ParamEyeBallY={Value("ParamEyeBallY"):0.000}" +
                    Environment.NewLine +
                    $"ParamAngleX={Value("ParamAngleX"):0.000}  ParamAngleY={Value("ParamAngleY"):0.000}" +
                    Environment.NewLine +
                    $"滑杆：横向 {viewModel.EyeTrackingHorizontalGain:0.00} / 抬头 {viewModel.EyeTrackingUpGain:0.00} / " +
                    $"低头 {viewModel.EyeTrackingDownGain:0.00} / 偏移 {viewModel.EyeTrackingVerticalOffset:0.00}" +
                    Environment.NewLine + "（目标在画布外对应方向；低头时 ParamEyeBallY 应为负、ParamAngleY 为负附近）";
                viewModel.Storage.WriteText("eye-demo.log", text);
                viewModel.Log(
                    $"眼睛跟随验证（目标：{direction}）→ ParamEyeBallX={Value("ParamEyeBallX"):0.00} " +
                    $"ParamEyeBallY={Value("ParamEyeBallY"):0.00} ParamAngleX={Value("ParamAngleX"):0.00} " +
                    $"ParamAngleY={Value("ParamAngleY"):0.00}；滑杆：横向 {viewModel.EyeTrackingHorizontalGain:0.00} / " +
                    $"抬头 {viewModel.EyeTrackingUpGain:0.00} / 低头 {viewModel.EyeTrackingDownGain:0.00} / " +
                    $"偏移 {viewModel.EyeTrackingVerticalOffset:0.00}（已写 Data\\live2d\\eye-demo.log）");
            }
            catch
            {
                // 模型正在切换：忽略
            }
        });
    }

    // ===================== 开发调试：动捕链路自测 =====================

    /// <summary>
    /// 开发调试（<c>YLT_LIVE2D_MOCAP_SELFTEST=1</c>）：页面打开后自动挑内置摄像头开动捕、跑 6 秒，
    /// 把「枚举 / 启动 / FPS / 帧尺寸 / 处理耗时 / 有脸次数 / 预览次数 / 停止」逐项写成
    /// <c>Data\live2d\mocap-selftest.log</c>（只写数字和设备名，<b>不写摄像头画面</b>）。
    /// 加 <c>YLT_LIVE2D_MOCAP_SELFTEST_EXIT=1</c> 时写完后退出程序（自动验收用）。
    /// </summary>
    private void TryBeginMocapSelfTest()
    {
        if (_mocapSelfTestStarted) return;
        if (Environment.GetEnvironmentVariable("YLT_LIVE2D_MOCAP_SELFTEST") is not ("1" or "on" or "true")) return;
        if (_viewModel is null || !IsPageOrPetVisible) return;
        _mocapSelfTestStarted = true;
        _ = RunMocapSelfTestAsync();
    }

    private async Task RunMocapSelfTestAsync()
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;

        var lines = new List<string>();
        void Line(string text)
        {
            lines.Add(text);
            viewModel.Log("[动捕自测] " + text);
        }

        var exitAfter = Environment.GetEnvironmentVariable("YLT_LIVE2D_MOCAP_SELFTEST_EXIT") is "1" or "on" or "true";
        var cfg = viewModel.Cfg;
        var originalEnabled = cfg.MocapEnabled;

        try
        {
            // ① 枚举摄像头
            var cameras = await MocapCameraSource.EnumerateCamerasAsync();
            Line(cameras.Count > 0
                ? $"OK 枚举摄像头：{cameras.Count} 个（{string.Join("、", cameras.Select(camera => camera.Name))}）"
                : "FAIL 枚举摄像头：0 个（检查设备管理器 / Windows 隐私设置是否允许桌面应用访问摄像头）");
            if (cameras.Count == 0) return;

            // ② 挑摄像头：优先内置 "HD User Facing"，尽量避开 DroidCam / 虚拟摄像头
            var chosen = Live2DViewModel.PickPreferredCamera(cameras) ?? cameras[0];
            cfg.MocapCameraId = chosen.Id;
            cfg.MocapCameraName = chosen.Name;
            cfg.MocapEnabled = true;
            cfg.MocapShowPreview = true;
            viewModel.RefreshAllBindings();
            _ = viewModel.EnsureCamerasLoadedAsync(force: true);
            ApplyRuntimeSettings();
            ApplyMocapSettings();

            // ③ 等启动（最多 12 秒；失败时 MocapStartError 有原因）
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 12_000 && !IsMocapRunning && string.IsNullOrEmpty(MocapStartError))
            {
                await Task.Delay(100);
            }

            var source = _mocap;
            if (source is null || !source.IsRunning)
            {
                Line($"FAIL StartAsync：未运行（{MocapStartError}{(string.IsNullOrEmpty(MocapStartError) ? "超时" : "")}）");
                return;
            }

            Line($"OK StartAsync：{chosen.Name}；LastError={(source.LastError ?? "无")}");

            // ④ 采样 6 秒（只统计数字，不保存画面）。
            // 放在后台线程上用 Thread.Sleep 计时（不用 Task.Delay：UI/线程池忙时定时器续体会被拖延，
            // 之前实测 6 秒只跑出 7 次采样；独占线程的 Sleep 不受影响，能稳定 200ms 一次）。
            var sample = await Task.Run(() =>
            {
                var total = 0;
                var latestCount = 0;
                var faceCount = 0;
                var previewCount = 0;
                long previewB = 0, previewG = 0, previewR = 0;
                var previewColorSamples = 0;
                double maxFpsLocal = 0, maxProcessLocal = 0, maxDetectLocal = 0;
                var frameSizeLocal = (0, 0);
                var sampled = Stopwatch.StartNew();
                while (sampled.ElapsedMilliseconds < 6_000)
                {
                    Thread.Sleep(200);
                    total++;

                    var latest = source.Latest;
                    if (latest is not null) latestCount++;
                    if (latest?.HasFace == true) faceCount++;
                    var preview = source.LatestPreview;
                    if (preview is not null)
                    {
                        previewCount++;
                        // 颜色自检：稀疏采样算 RGB 均值（只算数字，不保存画面）。
                        // 修复后三通道应接近（灰调画面）；若把 YUV 当 BGRA 读会 G 明显偏高、B/R 偏低（历史「绿油油」bug）。
                        for (var i = 0; i + 3 < preview.Bgra.Length; i += 4 * 37)
                        {
                            previewB += preview.Bgra[i];
                            previewG += preview.Bgra[i + 1];
                            previewR += preview.Bgra[i + 2];
                            previewColorSamples++;
                        }
                    }

                    maxFpsLocal = Math.Max(maxFpsLocal, source.MeasuredFps);
                    var size = source.FrameSize;
                    if (size.Item1 > 0) frameSizeLocal = size;
                    maxProcessLocal = Math.Max(maxProcessLocal, source.LastProcessMilliseconds);
                    maxDetectLocal = Math.Max(maxDetectLocal, source.LastDetectMilliseconds);
                }

                return (total, latestCount, faceCount, previewCount,
                    maxFpsLocal, maxProcessLocal, maxDetectLocal, frameSizeLocal,
                    previewB, previewG, previewR, previewColorSamples);
            });

            var (total, latestCount, faceCount, previewCount, maxFps, maxProcessMs, maxDetectMs, frameSize,
                previewB, previewG, previewR, previewColorSamples) = sample;

            Line($"OK 6 秒采样 {total} 次：MeasuredFps 最大 {maxFps:0.0}；FrameSize {frameSize.Item1}×{frameSize.Item2}；" +
                 $"单帧处理耗时最大 {maxProcessMs:0.0} ms（其中人脸检测 {maxDetectMs:0.0} ms）");
            Line((latestCount > 0 ? "OK" : "FAIL") + $" Latest 非 null：{latestCount}/{total} 次");
            Line($"OK 人脸采样：{faceCount}/{total} 次（房间没人时 0 也正常，这条只验证检测管线在跑）");
            Line((previewCount > 0 ? "OK" : "FAIL") +
                 $" LatestPreview 非 null：{previewCount}/{total} 次（已启用预览，应该有）");
            if (previewColorSamples > 0)
            {
                var meanB = previewB / (double)previewColorSamples;
                var meanG = previewG / (double)previewColorSamples;
                var meanR = previewR / (double)previewColorSamples;
                // 灰调画面三通道应接近；早先「把 YUV 当 BGRA 读」的 bug 会让 G 远高于 B/R（画面发绿）。
                var colorBalanced = Math.Abs(meanG - (meanB + meanR) / 2) < 45;
                Line($"{(colorBalanced ? "OK" : "WARN")} 预览颜色：平均 B={meanB:0.0} G={meanG:0.0} R={meanR:0.0}（三通道接近 = 颜色正常；G 明显偏高 = 发绿错色）");
            }

            // ⑤ 停止
            var stopWatch = Stopwatch.StartNew();
            await source.StopAsync();
            stopWatch.Stop();
            Line((!source.IsRunning ? "OK" : "FAIL") +
                 $" StopAsync：耗时 {stopWatch.ElapsedMilliseconds} ms，IsRunning={source.IsRunning}");
        }
        catch (Exception ex)
        {
            Line("EXCEPTION " + ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            // 恢复用户原来的配置（隐私：自测完不留下开着的摄像头）
            cfg.MocapEnabled = originalEnabled;
            ApplyRuntimeSettings();
            ApplyMocapSettings();
            viewModel.RefreshAllBindings();

            var report = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            try { viewModel.Storage.WriteText("mocap-selftest.log", report); }
            catch (Exception ex) { viewModel.Log("动捕自测日志写入失败：" + ex.Message); }

            if (exitAfter)
            {
                await Task.Delay(400);
                Application.Current?.Shutdown();
            }
        }
    }

    // ===================== 桌宠窗口 =====================

    /// <summary>显示桌宠（模型没加载就开始加载；加载完成前窗口是透明的）。</summary>
    public void ShowPet()
    {
        if (_shutdown) return;
        EnsureInitialized();
        HookWindowClose();

        var vm = EnsureViewModel();
        vm.Cfg.PetEnabled = true;
        EnsureModelLoaded();

        if (_pet is not null)
        {
            try { _pet.RefreshFromConfig(); } catch { /* 忽略 */ }
            UpdateFrameLoop();
            return;
        }

        try
        {
            var pet = new PetWindow(this);
            pet.Closed += OnPetClosed;
            _pet = pet;
            pet.Show();
            UpdateFrameLoop();
            ApplyMocapSettings();   // 桌宠也算「有人看着」→ 动捕可以开始采集
            StateChanged?.Invoke(this, EventArgs.Empty);
            PetVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _pet = null;
            vm.Log("桌宠窗口打开失败：" + ex.Message);
        }
    }

    /// <summary>隐藏桌宠（页面也关着时帧循环随之停下，摄像头也随之关闭）。</summary>
    public void HidePet()
    {
        if (_viewModel is not null) _viewModel.Cfg.PetEnabled = false;
        ClosePetInternal(raiseEvent: true);
        UpdateFrameLoop();
        ApplyMocapSettings();
    }

    /// <summary>显示 / 隐藏桌宠。</summary>
    public void TogglePet()
    {
        if (_pet is not null) HidePet();
        else ShowPet();
    }

    /// <summary>桌宠居中到主屏工作区。</summary>
    public void CenterPet()
    {
        try
        {
            _pet?.CenterOnScreen();
            _viewModel?.SetStatus("桌宠已居中");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("桌宠居中失败：" + ex.Message);
        }
    }

    /// <summary>桌宠回到默认位置（主屏右下角）并恢复默认大小。</summary>
    public void ResetPetPosition()
    {
        try
        {
            _pet?.ResetPositionToDefault();
            _viewModel?.SetStatus("桌宠位置已重置");
        }
        catch (Exception ex)
        {
            _viewModel?.Log("重置桌宠位置失败：" + ex.Message);
        }
    }

    /// <summary>配置变化后让桌宠窗口重新套用外观（宽高 / 不透明度 / 置顶 / 点击穿透）。</summary>
    public void ApplyPetConfig()
    {
        try { _pet?.RefreshFromConfig(); } catch { /* 忽略 */ }
    }

    private void ClosePetInternal(bool raiseEvent)
    {
        var pet = _pet;
        if (pet is null) return;
        _pet = null;
        try { pet.Closed -= OnPetClosed; } catch { /* 忽略 */ }
        try { pet.CloseForShutdown(); } catch { /* 忽略 */ }
        try { pet.Close(); } catch { /* 忽略 */ }
        UpdateFrameLoop();
        ApplyMocapSettings();
        if (!raiseEvent) return;
        StateChanged?.Invoke(this, EventArgs.Empty);
        PetVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPetClosed(object? sender, EventArgs e)
    {
        if (sender is not PetWindow pet || !ReferenceEquals(pet, _pet)) return;
        _pet = null;
        if (_viewModel is not null) _viewModel.Cfg.PetEnabled = false;
        SaveConfigNow();
        UpdateFrameLoop();
        ApplyMocapSettings();   // 桌宠关了、页面也没开 → 关闭摄像头

        // 桌宠关了、页面也没开：眼神回正
        if (!_pageActive)
        {
            try { _runtime?.ClearEyeTrackingTarget(); } catch { /* 忽略 */ }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        PetVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===================== 生命周期 / 退出 =====================

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (Application.Current is { } app) app.Exit += OnApplicationExit;
        }
        catch { /* 无 Application（理论上不会发生）忽略 */ }
        HookWindowClose();
    }

    /// <summary>挂主窗口关闭事件：主窗口一关就释放桌宠，否则最后一个窗口永远不关、进程退不掉。</summary>
    private void HookWindowClose()
    {
        if (_windowCloseHooked) return;
        if (Application.Current?.MainWindow is not { } window) return;
        _windowCloseHooked = true;
        try { window.Closed += OnMainWindowClosed; } catch { /* 忽略 */ }
    }

    private void UnhookWindowClose()
    {
        if (!_windowCloseHooked) return;
        _windowCloseHooked = false;
        try
        {
            if (Application.Current?.MainWindow is { } window) window.Closed -= OnMainWindowClosed;
        }
        catch { /* 忽略 */ }
    }

    private void OnApplicationExit(object? sender, ExitEventArgs e) => Shutdown();

    private void OnMainWindowClosed(object? sender, EventArgs e) => Shutdown();

    // ===================== 日志快捷方式 =====================

    /// <summary>往工具页日志里追加一行（可能在后台线程调用）。</summary>
    public void Log(string message) => _viewModel?.Log(message);
}
