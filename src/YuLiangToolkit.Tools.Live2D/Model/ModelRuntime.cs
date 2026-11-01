using System.Diagnostics;
using YuLiangToolkit.Tools.Live2D.Interop;
using YuLiangToolkit.Tools.Live2D.Rendering;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 模型运行时（工具页与桌宠共用）：加载资源 → 每帧 <see cref="Tick"/>（动作 / 表情 / 眼睛跟随 / 物理）
/// → <see cref="RenderFrame"/> 出图。
///
/// <para><b>加载顺序</b>（耗时大头是 5 张纹理）：moc3 → 模型 → cdi3 → 动作 / 表情解析 → 物理 → 纹理 → 渲染器。
/// 可以在后台线程调用 <see cref="Load"/>，加载完后再在 UI 线程 <see cref="Tick"/> / <see cref="RenderFrame"/>。</para>
///
/// <para><b>每帧顺序</b>（和官方 Cubism 更新管线一致）：
/// 受管参数恢复默认 → 待机行为（眨眼 / 呼吸）+ 眼睛跟随 → 动作混合 → 表情叠加 → 物理 → Core <c>Update()</c> → 触发 <see cref="FrameUpdated"/>。
/// 「受管参数」= 会被动作 / 表情 / 物理 / 待机 / 眼睛跟随写到的参数；其余参数（大部分纯调试参数）不会被每帧重置，
/// 所以工具页手动拖滑块在那些参数上是有效的。</para>
///
/// <para>加载完成前 <see cref="Model"/> / <see cref="Renderer"/> 会抛 <see cref="InvalidOperationException"/>；先判 <see cref="IsLoaded"/>。</para>
/// </summary>
public sealed class ModelRuntime : IDisposable
{
    /// <summary>输入 / 输出权重常量，眼控参数名集中放这里（见 <see cref="EyeTrackingController"/> 的注释）。</summary>
    private static readonly string[] EyeTrackingParameterIds =
    {
        "ParamAngleX", "ParamAngleY", "ParamAngleZ",
        "AngleX", "AngleY", "AngleZ",
        "ParamEyeBallX", "ParamEyeBallY",
        "ParamBodyAngleX", "ParamBodyAngleY", "ParamBodyAngleZ",
        "PositionX", "PositionY", "PositionZ",
    };

    private readonly MotionPlayer _motionPlayer = new();
    private readonly ExpressionManager _expressionManager = new();
    private readonly Dictionary<string, Motion3Data> _motionDataByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Expression3Data> _expressionDataByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly RuntimeLoadOptions _options;

    private CubismMoc? _moc;
    private CubismModel? _model;
    private TextureAtlasSet? _textures;
    private CubismRenderer? _renderer;
    private CubismPhysicsEvaluator? _physics;
    private IdleBehavior? _idle;
    private EyeTrackingController? _eye;
    private Live2DModelAsset? _asset;
    private DisplayInfo? _display;
    private MotionEntry[] _motions = Array.Empty<MotionEntry>();
    private ExpressionEntry[] _expressions = Array.Empty<ExpressionEntry>();
    private ParameterBinding[] _parameterBindings = Array.Empty<ParameterBinding>();
    private PartBinding[] _partBindings = Array.Empty<PartBinding>();
    private string[] _eyeBlinkParameterIds = Array.Empty<string>();
    private string[] _lipSyncParameterIds = Array.Empty<string>();
    private int[] _managedParameterIndices = Array.Empty<int>();
    private bool _disposed;

    private double _time;
    private double _lastTickMilliseconds;
    private RenderStats _lastRenderStats = RenderStats.Empty;

    private ModelRuntime(RuntimeLoadOptions options) => _options = options;

    // ---------- 状态 / 数据 ----------

    /// <summary>是否加载完成。</summary>
    public bool IsLoaded => _model is not null;

    /// <summary>模型名（显示用）。</summary>
    public string ModelName => _asset?.Name ?? "";

    /// <summary>模型资源信息。</summary>
    public Live2DModelAsset? Asset => _asset;

    /// <summary>cdi3 显示信息（没文件时为 null）。</summary>
    public DisplayInfo? Display => _display;

    /// <summary>底层模型（渲染器读它的变形数据）。</summary>
    public CubismModel Model => _model ?? throw new InvalidOperationException("模型尚未加载（先调用 ModelRuntime.Load）");

    /// <summary>渲染器（纹理 + 光栅化）。</summary>
    public CubismRenderer Renderer => _renderer ?? throw new InvalidOperationException("模型尚未加载（先调用 ModelRuntime.Load）");

    /// <summary>动作列表。</summary>
    public IReadOnlyList<MotionEntry> Motions => _motions;

    /// <summary>表情列表。</summary>
    public IReadOnlyList<ExpressionEntry> Expressions => _expressions;

    /// <summary>参数绑定列表（UI 参数页用）。</summary>
    public IReadOnlyList<ParameterBinding> Parameters => _parameterBindings;

    /// <summary>部件绑定列表（UI 部件页用）。</summary>
    public IReadOnlyList<PartBinding> Parts => _partBindings;

    /// <summary>最近一帧 Tick 耗时（毫秒）。</summary>
    public double LastTickMilliseconds => _lastTickMilliseconds;

    /// <summary>最近一帧渲染统计。</summary>
    public RenderStats LastRenderStats => _lastRenderStats;

    /// <summary>当前动作（没在播为 null）。</summary>
    public MotionEntry? CurrentMotion => _motionPlayer.Current?.Entry;

    /// <summary>当前动作已播放时间（秒）。</summary>
    public double CurrentMotionTime => _motionPlayer.CurrentTime;

    /// <summary>当前动作是否在循环。</summary>
    public bool CurrentMotionLoop => _motionPlayer.CurrentLoop;

    /// <summary>有没有加载物理（physics3.json 解析成功）。</summary>
    public bool HasPhysics => _physics is not null;

    /// <summary>模型运行总时长（秒，Tick 累计）。</summary>
    public double TimeSeconds => _time;

    // ---------- 设置 ----------

    public bool PhysicsEnabled { get; set; } = true;
    public double PhysicsStrength { get; set; } = 1.0;
    public bool IdleEnabled { get; set; } = true;
    public bool AutoBlink { get; set; } = true;
    public bool EyeTrackingEnabled { get; set; } = true;
    public double EyeTrackingSmoothing { get; set; } = 0.35;
    public double EyeTrackingHeadStrength { get; set; } = 1.0;
    public double EyeTrackingEyeStrength { get; set; } = 1.0;
    public double EyeTrackingBodyStrength { get; set; } = 0.5;

    /// <summary>默认待机动作（IdleEnabled 时循环播放；null = 不播）。</summary>
    public MotionEntry? IdleMotion { get; set; }

    // ---------- 生命周期 ----------

    /// <summary>加载模型（耗时：解码 5 张纹理 + 解析动作 / 物理；可在后台线程调）。</summary>
    public static ModelRuntime Load(Live2DModelAsset asset, RuntimeLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(asset);
        options ??= new RuntimeLoadOptions();

        var runtime = new ModelRuntime(options);
        try
        {
            runtime.LoadCore(asset, options);
            return runtime;
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    private void LoadCore(Live2DModelAsset asset, RuntimeLoadOptions options)
    {
        _asset = asset;
        var log = options.Log;
        var cancellationToken = options.CancellationToken;

        log?.Invoke($"开始加载模型「{asset.Name}」：{asset.Directory}");
        ReportProgress(0.01);
        cancellationToken.ThrowIfCancellationRequested();

        // 1) moc3 → 模型
        _moc = CubismMoc.Load(asset.MocPath);
        log?.Invoke($"moc3 版本 {_moc.MocVersion}（{CubismMoc.DescribeMocVersion(_moc.MocVersion)}），" +
                    $"Core {CubismMoc.GetCoreVersionText()}，一致性 {( _moc.IsConsistent ? "通过" : "未通过")}");
        ReportProgress(0.08);
        cancellationToken.ThrowIfCancellationRequested();

        _model = CubismModel.Create(_moc);
        log?.Invoke($"画布 {_model.CanvasWidthPixels:0}×{_model.CanvasHeightPixels:0}，" +
                    $"参数 {_model.ParameterCount} / 部件 {_model.PartCount} / 网格 {_model.DrawableCount} / 纹理 {_model.TexturesUsed} 张");
        ReportProgress(0.14);
        cancellationToken.ThrowIfCancellationRequested();

        // 2) cdi3 显示信息 + 参数 / 部件绑定
        _display = DisplayInfoLoader.TryLoad(asset.Cdi3Path, log);
        _parameterBindings = DisplayInfoLoader.BuildParameterBindings(_model, _display);
        _partBindings = DisplayInfoLoader.BuildPartBindings(_model, _display);
        log?.Invoke(_display is null
            ? "没有 cdi3.json，参数 / 部件名用 Id 显示"
            : $"cdi3：{_display.ParameterGroups.Count} 个参数分组，{_display.PartNames.Count} 个部件名");
        ReportProgress(0.18);

        // 3) 动作 / 表情解析
        LoadMotions(asset, log, cancellationToken);
        LoadExpressions(asset, log, cancellationToken);
        ReportProgress(0.26);
        cancellationToken.ThrowIfCancellationRequested();

        // 4) 物理
        if (!string.IsNullOrWhiteSpace(asset.PhysicsPath) && File.Exists(asset.PhysicsPath))
        {
            try
            {
                var rig = PhysicsRig.Load(asset.PhysicsPath);
                _physics = new CubismPhysicsEvaluator(_model, rig);
                if (_physics.UnresolvedInputs > 0 || _physics.UnresolvedOutputs > 0)
                {
                    log?.Invoke($"物理配置里有 {_physics.UnresolvedInputs} 个输入参数 / {_physics.UnresolvedOutputs} 个输出参数在模型里找不到（已跳过）");
                }

                _physics.Stabilize();
                log?.Invoke($"物理：{_physics.SubRigCount} 个子组 / {_physics.OutputCount} 个输出参数 / {_physics.ParticleCount} 个粒子（{_physics.Fps:0} FPS）");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                log?.Invoke($"physics3 解析失败（物理关闭）：{exception.Message}");
            }
        }
        else
        {
            log?.Invoke("没有 physics3.json，物理关闭");
        }

        // 5) 纹理 → 6) 渲染器
        if (asset.TexturePaths.Count == 0) throw new InvalidOperationException("模型没有任何纹理（model3.json Textures 为空）");
        _textures = TextureAtlasSet.Load(asset.TexturePaths, options.TextureMaxSize,
            progress => ReportProgress(0.26 + (progress * 0.68)), cancellationToken);
        log?.Invoke($"纹理：{_textures.Count} 张，最大边 {options.TextureMaxSize}");
        ReportProgress(0.95);
        cancellationToken.ThrowIfCancellationRequested();
        _renderer = new CubismRenderer(_textures);

        // 7) 待机 / 眼睛跟随控制器 + 受管参数表
        _eyeBlinkParameterIds = ResolveEyeBlinkIds(asset);
        _lipSyncParameterIds = asset.LipSyncParameterIds.ToArray();
        _idle = new IdleBehavior(_model, _eyeBlinkParameterIds);
        _eye = new EyeTrackingController(_model);
        _managedParameterIndices = BuildManagedParameterIndices();

        // 8) 默认待机动作
        IdleMotion = ResolveIdleMotion(options.IdleMotionName);
        log?.Invoke($"动作 {_motions.Length} 个 / 表情 {_expressions.Length} 个" +
                    (IdleMotion is null ? "（没有待机动作）" : $"；待机动作 = {IdleMotion.Name}"));

        ReportProgress(1.0);
        log?.Invoke("模型加载完成");
    }

    private void LoadMotions(Live2DModelAsset asset, Action<string>? log, CancellationToken cancellationToken)
    {
        var motions = new List<MotionEntry>(asset.Motions.Count);
        foreach (var entry in asset.Motions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var data = Motion3Data.Load(entry.FilePath);
                _motionDataByPath[entry.FilePath] = data;
                motions.Add(entry);
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                log?.Invoke($"动作解析失败（跳过 {entry.Name}）：{exception.Message}");
            }
        }

        _motions = motions.ToArray();
    }

    private void LoadExpressions(Live2DModelAsset asset, Action<string>? log, CancellationToken cancellationToken)
    {
        var expressions = new List<ExpressionEntry>(asset.ExpressionPaths.Count);
        foreach (var path in asset.ExpressionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            name = name[..^".exp3.json".Length];
            var entry = new ExpressionEntry { Name = name, FilePath = path };
            try
            {
                _expressionDataByPath[path] = Expression3Data.Load(path);
                expressions.Add(entry);
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                log?.Invoke($"表情解析失败（跳过 {name}）：{exception.Message}");
            }
        }

        _expressions = expressions.ToArray();
    }

    private MotionEntry? ResolveIdleMotion(string? preferredName)
    {
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var match = _motions.FirstOrDefault(m =>
                string.Equals(m.Name, preferredName, StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return _motions.FirstOrDefault(m => m.IsIdleCandidate) ?? _motions.FirstOrDefault();
    }

    private string[] ResolveEyeBlinkIds(Live2DModelAsset asset)
    {
        var ids = new List<string>();
        foreach (var id in asset.EyeBlinkParameterIds)
        {
            if (_model?.FindParameter(id) is not null) ids.Add(id);
        }

        if (ids.Count == 0 && _model is not null)
        {
            foreach (var candidate in new[] { "ParamEyeLOpen", "ParamEyeROpen" })
            {
                if (_model.FindParameter(candidate) is not null) ids.Add(candidate);
            }
        }

        return ids.ToArray();
    }

    /// <summary>收集「运行时每帧会改写」的参数下标（每帧只把这些参数恢复默认）。</summary>
    private int[] BuildManagedParameterIndices()
    {
        var model = _model!;
        var managed = new HashSet<int>();

        void Add(string id)
        {
            var parameter = model.FindParameter(id);
            if (parameter is not null) managed.Add(parameter.Index);
        }

        // 动作曲线
        foreach (var data in _motionDataByPath.Values)
        {
            foreach (var curve in data.Curves)
            {
                if (curve.Target == MotionCurveTarget.Parameter || curve.Target == MotionCurveTarget.PartOpacity)
                    Add(curve.Id);
            }
        }

        // 表情参数
        foreach (var data in _expressionDataByPath.Values)
        {
            foreach (var parameter in data.Parameters) Add(parameter.Id);
        }

        // 物理输出
        if (_physics is not null)
        {
            foreach (var output in _physics.Outputs) Add(output.DestinationId);
        }

        // 待机 / 眼睛跟随
        foreach (var id in EyeTrackingParameterIds) Add(id);
        if (model.FindParameter("ParamBreath") is not null) Add("ParamBreath");
        foreach (var id in _eyeBlinkParameterIds) Add(id);

        var indices = new int[managed.Count];
        managed.CopyTo(indices);
        Array.Sort(indices);
        return indices;
    }

    /// <summary>推进一帧（在 UI 线程的 DispatcherTimer 里调）。</summary>
    public void Tick(TimeSpan delta)
    {
        var model = _model;
        if (model is null || _disposed) return;

        var startTimestamp = Stopwatch.GetTimestamp();
        var deltaSeconds = Math.Clamp(delta.TotalSeconds, 0.0, 0.25);
        _time += deltaSeconds;

        var parameters = model.Parameters;
        var managed = _managedParameterIndices;
        for (var i = 0; i < managed.Length; i++)
        {
            var parameter = parameters[managed[i]];
            parameter.Value = parameter.DefaultValue;
        }

        // 待机行为（眨眼 / 呼吸）与眼睛跟随：摇摆量在这里合并，避免两处抢写同一个参数
        var idleEnabled = IdleEnabled;
        var eye = _eye;
        if (eye is not null)
        {
            eye.Smoothing = EyeTrackingSmoothing;
            eye.HeadStrength = EyeTrackingHeadStrength;
            eye.EyeStrength = EyeTrackingEyeStrength;
            eye.BodyStrength = EyeTrackingBodyStrength;
        }

        if (_idle is not null)
        {
            _idle.Update(_time, deltaSeconds, AutoBlink, idleEnabled, idleEnabled);
        }

        if (eye is not null && (idleEnabled || EyeTrackingEnabled))
        {
            eye.Update(deltaSeconds, EyeTrackingEnabled,
                idleEnabled ? _idle?.SwayAngleZ ?? 0f : 0f,
                idleEnabled ? _idle?.SwayBodyAngleZ ?? 0f : 0f);
        }

        // 待机动作（IdleEnabled 且当前没有动作在播时循环播放）
        if (idleEnabled && IdleMotion is { } idleMotion && !_motionPlayer.HasActive &&
            _motionDataByPath.TryGetValue(idleMotion.FilePath, out var idleData))
        {
            _motionPlayer.Play(model, idleMotion, idleData, loop: true);
        }

        _motionPlayer.Update(model, deltaSeconds, _eyeBlinkParameterIds, _lipSyncParameterIds);
        _expressionManager.Update(model, deltaSeconds);

        // 物理：在参数都算完之后、Core 求值之前跑（改的是参数值）
        if (PhysicsEnabled && _physics is not null)
        {
            _physics.Evaluate(deltaSeconds, (float)Math.Clamp(PhysicsStrength, 0.0, 1.0));
        }

        model.Update();
        _lastTickMilliseconds = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        FrameUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把当前帧画到渲染器的位图上（在 UI 线程调）。</summary>
    public void RenderFrame(RenderOptions options)
    {
        if (_disposed) return;
        var renderer = _renderer;
        var model = _model;
        if (renderer is null || model is null) throw new InvalidOperationException("模型尚未加载（先调用 ModelRuntime.Load）");

        renderer.Render(model, options);
        _lastRenderStats = renderer.LastStats;
    }

    // ---------- 控制 ----------

    /// <summary>眼睛 / 头部跟随到画布像素坐标（工具页鼠标位置 / 桌宠的全局鼠标换算）。</summary>
    public void SetEyeTrackingTarget(float canvasX, float canvasY) => _eye?.SetTarget(canvasX, canvasY);

    /// <summary>停止眼睛跟随（回到默认朝向）。</summary>
    public void ClearEyeTrackingTarget() => _eye?.ClearTarget();

    /// <summary>
    /// 播放动作（<paramref name="loop"/> = null 时用动作自带 Loop 建议）。
    /// <paramref name="fadeSeconds"/>：淡入 / 交叉淡化秒数；&lt; 0 = 用动作自带的 FadeInTime，0 = 立即切换。
    /// </summary>
    public void PlayMotion(MotionEntry motion, bool? loop = null, double fadeSeconds = 0.3)
    {
        ArgumentNullException.ThrowIfNull(motion);
        var model = _model;
        if (model is null) return;
        if (!_motionDataByPath.TryGetValue(motion.FilePath, out var data))
        {
            _options.Log?.Invoke($"动作没有解析数据，无法播放：{motion.Name}");
            return;
        }

        _motionPlayer.Play(model, motion, data, loop ?? motion.Loop, fadeSeconds);
    }

    /// <summary>停止当前动作（按 <paramref name="fadeSeconds"/> 淡出；≤0 = 立即）。</summary>
    public void StopMotion(double fadeSeconds = 0.25) => _motionPlayer.Stop(fadeSeconds);

    /// <summary>施加表情（按 fadeSeconds 淡入；≤0 = 立即）。</summary>
    public void ApplyExpression(ExpressionEntry expression, double fadeSeconds = 0.3)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!_expressionDataByPath.TryGetValue(expression.FilePath, out var data))
        {
            _options.Log?.Invoke($"表情没有解析数据，无法施加：{expression.Name}");
            return;
        }

        _expressionManager.Apply(expression, data, fadeSeconds);
    }

    /// <summary>清除所有表情（按 fadeSeconds 淡出）。</summary>
    public void ClearExpressions(double fadeSeconds = 0.3) => _expressionManager.Clear(fadeSeconds);

    /// <summary>某个表情当前是否生效（UI 勾选状态用）。</summary>
    public bool IsExpressionActive(ExpressionEntry expression) => _expressionManager.IsActive(expression);

    /// <summary>把所有参数 / 部件恢复默认（表情 / 动作也停掉）。</summary>
    public void ResetParameters()
    {
        var model = _model;
        if (model is null) return;

        model.ResetParametersToDefault();
        model.ResetPartOpacities();
        _motionPlayer.Clear();
        _expressionManager.ClearImmediate();
        _physics?.Reset();
        _idle?.Reset(_time);
        _eye?.ClearTarget();
        model.Update();
    }

    /// <summary>重新找待机动作（改完 IdleMotion 或想立刻恢复待机时调）。</summary>
    public void ReloadIdle()
    {
        if (IdleMotion is null || !_motions.Contains(IdleMotion))
            IdleMotion = _motions.FirstOrDefault(m => m.IsIdleCandidate) ?? _motions.FirstOrDefault();

        // 下一帧 Tick 发现没有动作在播就会自动开始待机
        if (_motionPlayer.HasActive && CurrentMotion?.IsIdleCandidate == true) _motionPlayer.Stop();
    }

    /// <summary>每帧 Tick 完触发（UI 刷新参数值 / 渲染照它来）。</summary>
    public event EventHandler? FrameUpdated;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        FrameUpdated = null;
        _renderer?.Dispose();
        _renderer = null;
        _textures?.Dispose();
        _textures = null;
        _model?.Dispose();
        _model = null;
        _moc?.Dispose();
        _moc = null;
        _physics = null;
        _idle = null;
        _eye = null;
        _motionDataByPath.Clear();
        _expressionDataByPath.Clear();
        _motionPlayer.Clear();
        _expressionManager.ClearImmediate();
        _motions = Array.Empty<MotionEntry>();
        _expressions = Array.Empty<ExpressionEntry>();
        _parameterBindings = Array.Empty<ParameterBinding>();
        _partBindings = Array.Empty<PartBinding>();
        _eyeBlinkParameterIds = Array.Empty<string>();
        _lipSyncParameterIds = Array.Empty<string>();
        _managedParameterIndices = Array.Empty<int>();
    }

    private void ReportProgress(double progress) => _options.Progress?.Invoke(Math.Clamp(progress, 0.0, 1.0));
}
