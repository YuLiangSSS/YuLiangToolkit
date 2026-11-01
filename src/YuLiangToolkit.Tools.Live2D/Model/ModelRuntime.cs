using System.Diagnostics;
using YuLiangToolkit.Tools.Live2D.Audio;
using YuLiangToolkit.Tools.Live2D.Interop;
using YuLiangToolkit.Tools.Live2D.Mocap;
using YuLiangToolkit.Tools.Live2D.Rendering;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 模型运行时（工具页与桌宠共用）：加载资源 → 每帧 <see cref="Tick"/>（动捕 / 身体晃动 / 动作 / 表情 / 眼睛跟随 / 物理）
/// → <see cref="RenderFrame"/> 出图。
///
/// <para><b>加载顺序</b>（耗时大头是 5 张纹理）：moc3 → 模型 → cdi3 → 动作 / 表情解析 → 物理 → 纹理 → 渲染器。
/// 可以在后台线程调用 <see cref="Load"/>，加载完后再在 UI 线程 <see cref="Tick"/> / <see cref="RenderFrame"/>。</para>
///
/// <para><b>每帧顺序</b>（和官方 Cubism 更新管线一致）：
/// 受管参数恢复默认 → 待机行为（眨眼 / 呼吸）→ 摄像头动捕（有脸时）→ 眼睛跟随鼠标（动捕有脸时跳过）
/// → 语音口型（<see cref="SpeechActive"/>，动捕有脸时让位）→ 身体晃动测试（<see cref="BodySway"/>）
/// → 音乐跟唱 / 律动（<see cref="Music"/>，语音口型优先、音乐嘴型不出）
/// → 整身轻倾（<see cref="BodyLeanDegrees"/>，以脚为支点，渲染层旋转画布）
/// → 动作混合 → <b>手势 / 表演 / 舞蹈</b>（<see cref="GestureIds"/>，写在动作之后覆盖 IDLE 手臂曲线）
/// → 表情叠加 → 物理 → Core <c>Update()</c> → 触发 <see cref="FrameUpdated"/>。
/// 「受管参数」= 会被动作 / 表情 / 物理 / 待机 / 眼睛跟随 / 动捕 / 身体晃动 / 音乐 / 手势表演舞蹈 / 语音写到的参数；
/// 其余参数（大部分纯调试参数）不会被每帧重置，所以工具页手动拖滑块在那些参数上是有效的。</para>
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

    /// <summary>摄像头动捕会写的参数（受管参数用；眨眼 / 眼珠另行叠加）。</summary>
    private static readonly string[] MocapParameterIds =
    {
        "ParamAngleX", "ParamAngleY", "ParamAngleZ",
        "AngleX", "AngleY", "AngleZ",
        "ParamEyeBallX", "ParamEyeBallY",
        "ParamEyeLOpen", "ParamEyeROpen",
        "ParamMouthOpenY",
        "ParamBodyAngleX", "PositionX",
    };

    /// <summary>动捕有脸的有效期（毫秒）：超过这个时间没新结果就当没脸，恢复鼠标 / 待机。</summary>
    private const double MocapFaceTimeoutMs = 600.0;

    /// <summary>动捕时身体跟随头部的温和比例（Yaw × 该比例 → 身体左右）。</summary>
    private const float MocapBodyFollowRatio = 0.35f;

    /// <summary>动捕状态文案（不变时复用同一个字符串，避免每帧分配）。</summary>
    private const string MocapStatusNoData = "无数据";
    private const string MocapStatusTracking = "正在跟踪";
    private const string MocapStatusNoFace = "未检测到人脸";

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
    private BodySwayController? _bodySway;
    private IMocapProvider? _mocap;
    private Live2DModelAsset? _asset;
    private DisplayInfo? _display;
    private MotionEntry[] _motions = Array.Empty<MotionEntry>();
    private ExpressionEntry[] _expressions = Array.Empty<ExpressionEntry>();
    private ParameterBinding[] _parameterBindings = Array.Empty<ParameterBinding>();
    private PartBinding[] _partBindings = Array.Empty<PartBinding>();
    private string[] _eyeBlinkParameterIds = Array.Empty<string>();
    private string[] _lipSyncParameterIds = Array.Empty<string>();

    // 受管参数分组（预计算下标，每帧按开关决定哪些组要「恢复默认」；不再每帧重建数组）
    // 动作曲线不在这里：它只在「上一帧实际被动作写过」的参数上复位（见 ResetMotionTouchedParameters），
    // 这样待机动作只覆盖它自己那 21 个参数，用户手动调的脸 / 嘴参数不会被动作列表里别的文件拖累。
    private int[] _expressionManaged = Array.Empty<int>();
    private int[] _physicsManaged = Array.Empty<int>();
    private int[] _idleManaged = Array.Empty<int>();
    private int[] _blinkManaged = Array.Empty<int>();
    private int[] _eyeManaged = Array.Empty<int>();
    private int[] _mocapManaged = Array.Empty<int>();
    private int[] _swayManaged = Array.Empty<int>();

    // 子系统开关的上一帧状态（true → false 的那一帧把该组参数恢复默认，之后不再动它们，让手动值保留）
    private bool _managedGroupsInitialized;
    private bool _prevPhysicsEnabled;
    private bool _prevIdleEnabled;
    private bool _prevBlinkActive;
    private bool _prevEyeTrackingEnabled;
    private bool _prevMocapEnabled;
    private bool _prevSwayActive;
    private bool _prevExpressionActive;

    private bool _disposed;

    // 动捕缓存的参数（构造后每帧直接写，不查字典）
    private CubismParameter? _mocapParamAngleX;
    private CubismParameter? _mocapParamAngleY;
    private CubismParameter? _mocapParamAngleZ;
    private CubismParameter? _mocapAngleX;
    private CubismParameter? _mocapAngleY;
    private CubismParameter? _mocapAngleZ;
    private CubismParameter? _mocapEyeBallX;
    private CubismParameter? _mocapEyeBallY;
    private CubismParameter? _mocapEyeLOpen;
    private CubismParameter? _mocapEyeROpen;
    private CubismParameter? _mocapMouthOpenY;
    private CubismParameter? _mocapBodyAngleX;
    private CubismParameter? _mocapPositionX;

    // 动捕状态（给 UI 显示；状态文案只在变化时换引用，保持 0 分配）
    private bool _mocapHasFace;
    private double _mocapLastFaceAgeMs = -1;
    private int _mocapStatusKind = -1;
    private string _mocapStatusText = MocapStatusNoData;

    private double _swayDemoRemaining;

    // ---------- 音乐跟唱 / 律动 ----------

    private IMusicReactiveProvider? _music;
    private CubismParameter? _musicMouthOpenY;
    private CubismParameter? _musicMouthForm;
    private CubismParameter? _musicJawOpen;
    private CubismParameter? _musicParamAngleY;

    // 本帧音乐写过的参数（下一帧先复位这些；只有真正在写的那一帧才管理这些参数）
    private int[] _musicTouchedIndices = new int[16];
    private int _musicTouchedCount;

    private MusicContentKind _musicKind = MusicContentKind.Silent;
    private int _musicStatusKind = -1;
    private string _musicStatusText = MusicStatusSilent;

    /// <summary>这一帧音乐是否在播（供整身轻倾加速用）。</summary>
    private bool _musicPlaying;

    /// <summary>音频数据超过这个时间没更新就当「没在播」（采集帧率约 20Hz，留一点余量）。</summary>
    private const double MusicDataTimeoutMs = 800.0;

    private const string MusicStatusSilent = "静音";
    private const string MusicStatusAnalyzing = "识别中";
    private const string MusicStatusInstrumental = "纯音乐";
    private const string MusicStatusVocal = "带歌词";

    // ---------- 整身轻倾（脚为支点） ----------

    private const double BodyLeanIdlePeriodSeconds = 4.0;
    private const double BodyLeanIdleAmplitudeDegrees = 0.6;
    private const double BodyLeanMusicPeriodSeconds = 2.6;
    private const double BodyLeanMusicAmplitudeDegrees = 1.0;
    private const double BodyLeanSmoothingSeconds = 0.35;

    private double _bodyLeanPhase;
    private double _bodyLeanPeriod = BodyLeanIdlePeriodSeconds;
    private double _bodyLeanAmplitude = BodyLeanIdleAmplitudeDegrees;

    // ---------- 动作系统：手势 / 时间线表演 / 舞蹈 / 语音口型 / 待机随机小动作 ----------

    private GesturePlayer? _gesture;
    private PerformancePlayer? _performance;
    private DanceController? _dance;

    private CubismParameter? _speechMouthOpenY;
    private CubismParameter? _speechMouthForm;
    private CubismParameter? _speechJawOpen;
    private int[] _speechTouchedIndices = new int[8];
    private int _speechTouchedCount;
    private bool _speechActive;
    private float _speechMouthOpen;

    /// <summary>待机随机小动作的倒计时（秒；到 0 且条件允许时播一个随机手势 / 表演）。</summary>
    private double _idleRandomGestureTimer;

    /// <summary>固定种子的随机数（自检可重复；只用于挑待机小动作）。</summary>
    private readonly Random _idleRandomRng = new(20250301);

    private int _idleRandomGestureCount;

    /// <summary>待机随机小动作已触发次数（自检用）。</summary>
    internal int IdleRandomTriggerCount => _idleRandomGestureCount;

    /// <summary>上一帧是否处于「忙碌」状态（动捕 / 音乐 / 语音 / 演示 / 用户动作）；用于恢复待机后先冷却。</summary>
    private bool _idleRandomWasBusy;

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

    /// <summary>鼠标跟随横向灵敏度倍率（0.3~3.0；1 = 原手感，越大越跟手）。</summary>
    public double EyeTrackingHorizontalGain { get; set; } = 1.0;

    /// <summary>鼠标跟随「抬头」方向灵敏度倍率（0.3~3.0）。</summary>
    public double EyeTrackingUpGain { get; set; } = 1.0;

    /// <summary>鼠标跟随「低头」方向灵敏度倍率（0.3~3.0；觉得向下跟不到就调大）。</summary>
    public double EyeTrackingDownGain { get; set; } = 1.0;

    /// <summary>鼠标跟随的纵向中性偏移（-0.5~0.5；先加偏移，再做抬头 / 低头增益）。</summary>
    public double EyeTrackingVerticalOffset { get; set; }

    /// <summary>默认待机动作（IdleEnabled 时循环播放；null = 不播）。</summary>
    public MotionEntry? IdleMotion { get; set; }

    // ---------- 摄像头面部动捕 ----------

    /// <summary>
    /// 动捕数据源（摄像头 <see cref="MocapCameraSource"/> 或自检 / 演示用的假数据源）。
    /// 运行时每帧读一次 <see cref="IMocapProvider.Latest"/>；为 null 时按「无数据」处理。
    /// </summary>
    public IMocapProvider? Mocap
    {
        get => _mocap;
        set
        {
            _mocap = value;
            _mocapStatusKind = -1; // 换数据源后强制刷新一次状态文案
        }
    }

    /// <summary>是否启用动捕（关掉立即恢复鼠标眼控 / 自动眨眼）。</summary>
    public bool MocapEnabled { get; set; }

    /// <summary>动捕是否驱动头部转动。</summary>
    public bool MocapHeadEnabled { get; set; } = true;

    /// <summary>动捕是否驱动眨眼。</summary>
    public bool MocapBlinkEnabled { get; set; } = true;

    /// <summary>动捕是否驱动张嘴。</summary>
    public bool MocapMouthEnabled { get; set; } = true;

    /// <summary>头部转动幅度倍率（0 ~ 1.5）。</summary>
    public double MocapHeadStrength { get; set; } = 1.0;

    /// <summary>眼球跟随幅度倍率（0 ~ 1.5）。</summary>
    public double MocapEyeStrength { get; set; } = 1.0;

    /// <summary>张嘴开合幅度倍率（0 ~ 1.5）。</summary>
    public double MocapMouthStrength { get; set; } = 1.0;

    /// <summary>上一帧动捕有没有检测到有效人脸（给 UI 显示）。</summary>
    public bool MocapHasFace => _mocapHasFace;

    /// <summary>最近一帧动捕结果距今多少毫秒（没有数据为 -1；给 UI 显示延迟）。</summary>
    public double MocapLastFaceAgeMs => _mocapLastFaceAgeMs;

    /// <summary>动捕状态文案："无数据" / "正在跟踪" / "未检测到人脸"。</summary>
    public string MocapStatusText => _mocapStatusText;

    // ---------- 身体晃动测试 ----------

    /// <summary>身体晃动控制器（UI 可以读 <see cref="BodySwayController.LastApplied"/> / <see cref="BodySwayController.Phase"/>）。</summary>
    public BodySwayController? BodySway => _bodySway;

    /// <summary><see cref="BodySway"/> 的别名（UI 代码两种写法都支持）。</summary>
    public BodySwayController? Sway => _bodySway;

    /// <summary>是否开启身体晃动测试。</summary>
    public bool SwayEnabled { get; set; }

    /// <summary>晃动模式（<see cref="BodySwayController.Modes"/> 之一）。</summary>
    public string SwayMode { get; set; } = BodySwayController.ModeLeftRight;

    /// <summary>晃动幅度（度，0 ~ 30）。</summary>
    public double SwayAmplitudeDegrees { get; set; } = 12.0;

    /// <summary>晃动速度（Hz，0.05 ~ 2）。</summary>
    public double SwaySpeedHz { get; set; } = 0.5;

    /// <summary>头部反向补偿比例（0 = 头完全不动，1 = 完全反向抵消身体倾斜）。</summary>
    public double SwayHeadFollow { get; set; } = 0.35;

    /// <summary>是否连腰部 / 腿部一起晃。</summary>
    public bool SwayIncludeHips { get; set; } = true;

    /// <summary>「开始身体晃动测试」的剩余秒数（&gt;0 时即使 <see cref="SwayEnabled"/> 关着也会晃，到点自动停）。</summary>
    public double SwayDemoRemainingSeconds => _swayDemoRemaining;

    /// <summary>开始一段限时晃动测试（<paramref name="seconds"/> 秒后自动停；不影响 <see cref="SwayEnabled"/>）。</summary>
    public void StartSwayDemo(double seconds)
    {
        _swayDemoRemaining = Math.Clamp(seconds, 0.1, 3600.0);
        _bodySway?.Reset();
    }

    /// <summary>立即停止限时晃动测试。</summary>
    public void StopSwayDemo() => _swayDemoRemaining = 0;

    // ---------- 音乐跟唱 / 律动 ----------

    /// <summary>
    /// 音乐数据源（<c>Audio/MusicReactiveSource</c>：WASAPI 回环采集 + <c>MusicAnalyzer</c> 分析）。
    /// 运行时每帧读 <see cref="IMusicReactiveProvider.Latest"/>；为 null 时按「静音」处理。
    /// </summary>
    public IMusicReactiveProvider? Music
    {
        get => _music;
        set
        {
            _music = value;
            _musicStatusKind = -1; // 换数据源后强制刷新一次状态文案
        }
    }

    /// <summary>是否启用音乐跟唱 / 律动。</summary>
    public bool MusicEnabled { get; set; }

    /// <summary>识别模式（自动 / 强制跟唱 / 强制纯音乐）。</summary>
    public MusicVocalMode MusicMode { get; set; } = MusicVocalMode.Auto;

    /// <summary>跟唱嘴型幅度倍率（0 ~ 1.5）。</summary>
    public double MusicLipSyncStrength { get; set; } = 1.0;

    /// <summary>身体摇晃幅度倍率（0 ~ 1.5）。</summary>
    public double MusicSwayStrength { get; set; } = 1.0;

    /// <summary>音乐播放时是否轻微摇晃身体（纯音乐也晃）。</summary>
    public bool MusicSwayEnabled { get; set; } = true;

    /// <summary>音乐播放时是否轻微点头（被动捕 / 鼠标眼控占用头部时不写）。</summary>
    public bool MusicHeadBobEnabled { get; set; } = true;

    // ---------- 整身以脚为支点的左右轻倾（渲染层按 pivot=脚底 旋转 canvas） ----------

    /// <summary>是否启用整身轻倾（默认开；关闭时角度平滑回 0）。</summary>
    public bool BodyLeanEnabled { get; set; } = true;

    /// <summary>
    /// 当前整身轻倾角度（度，正负表示左右倾）。渲染层按「脚底为支点」旋转画布即可。
    /// 常态 4 秒周期 ±0.6°；音乐在播时平滑加快到约 2.6 秒、幅度约 1.0°×<see cref="MusicSwayStrength"/>。
    /// </summary>
    public float BodyLeanDegrees { get; private set; }

    /// <summary>最近一帧音乐内容类型（没在播 / 关闭时为 Silent；数据不足为 Unknown）。</summary>
    public MusicContentKind MusicKind => _musicKind;

    /// <summary>音乐状态文案："静音" / "识别中" / "纯音乐" / "带歌词"（不变时复用字符串）。</summary>
    public string MusicStatusText => _musicStatusText;

    // ---------- 动作系统：手势 / 表演 / 舞蹈 / 语音口型 / 待机随机小动作 ----------

    /// <summary>所有手势 id（3 个作者原生手势 + 5 个参数姿势，中文名）。</summary>
    public IReadOnlyList<string> GestureIds => _gesture?.Ids ?? Array.Empty<string>();

    /// <summary>当前手势 id（没有为 null）。</summary>
    public string? ActiveGesture => _gesture?.ActiveId;

    /// <summary>播一个手势（未知 id 返回 false；会先停动作 / 表演 / 舞蹈，避免抢手臂）。</summary>
    public bool PlayGesture(string id)
    {
        if (_gesture is null) return false;
        if (!_gesture.IsKnownId(id)) return false;
        // 先停其它演示 / 当前动作，再播手势：原生手势的 enter 动作不能被随后的 StopMotion 顶掉
        _performance?.Stop();
        _dance?.StopImmediate();
        StopMotion(0.12);
        return _gesture.Play(id, stopMotionFirst: false);
    }

    /// <summary>停止手势（原生手势播作者退出动作；参数姿势平滑淡出）。</summary>
    public void StopGesture() => _gesture?.Stop();

    /// <summary>所有时间线表演 id（中文名）。</summary>
    public IReadOnlyList<string> PerformanceIds => _performance?.Ids ?? Array.Empty<string>();

    /// <summary>当前表演 id（没有为 null）。</summary>
    public string? ActivePerformance => _performance?.ActiveId;

    /// <summary>播一个时间线表演（未知 id 返回 false；会先停手势 / 舞蹈 / 动作）。</summary>
    public bool PlayPerformance(string id)
    {
        if (_performance is null) return false;
        _gesture?.StopImmediate();
        _dance?.StopImmediate();
        StopMotion(0.12);
        return _performance.Play(id);
    }

    /// <summary>停止当前表演。</summary>
    public void StopPerformance() => _performance?.Stop();

    /// <summary>所有舞蹈模式。</summary>
    public IReadOnlyList<string> DanceModes => DanceController.Modes;

    /// <summary>当前舞蹈模式（淡出中仍返回模式名；完全停下为 null）。</summary>
    public string? ActiveDance => _dance?.ActiveMode;

    /// <summary>舞蹈速度倍率（0.5~2.0）。</summary>
    public double DanceSpeed
    {
        get => _dance?.Speed ?? 1.0;
        set
        {
            if (_dance is not null) _dance.Speed = value;
        }
    }

    /// <summary>开始跳舞（未知模式返回 false；会先停手势 / 表演 / 动作）。</summary>
    public bool StartDance(string mode, double speed = 1.0)
    {
        if (_dance is null) return false;
        _gesture?.StopImmediate();
        _performance?.Stop();
        StopMotion(0.12);
        return _dance.Start(mode, speed);
    }

    /// <summary>停止跳舞（0.25 秒权重淡出）。</summary>
    public void StopDance() => _dance?.Stop();

    /// <summary>停止所有演示动作（手势 / 表演 / 舞蹈 / 当前动作）。</summary>
    public void StopAllDemos()
    {
        _gesture?.StopImmediate();
        _performance?.Stop();
        _dance?.StopImmediate();
        StopMotion(0.2);
    }

    // ---------- 自检 / 调试用的内部只读观察口 ----------

    internal GesturePlayer? GestureForTest => _gesture;
    internal PerformancePlayer? PerformanceForTest => _performance;
    internal DanceController? DanceForTest => _dance;

    /// <summary>当前舞蹈相位（弧度；自检观察速度倍率用）。</summary>
    internal double ActiveDancePhase => _dance?.Phase ?? 0;

    /// <summary>是否正在语音说话（由会话 / UI 从 TTS 服务推入；优先级：动捕有脸 &gt; 语音 &gt; 音乐）。</summary>
    public bool SpeechActive
    {
        get => _speechActive;
        set => _speechActive = value;
    }

    /// <summary>语音口型开合（0~1；<see cref="SpeechActive"/> 为 true 时驱动嘴型）。</summary>
    public float SpeechMouthOpen
    {
        get => _speechMouthOpen;
        set => _speechMouthOpen = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>待机随机小动作开关（默认开；播动作 / 音乐 / 说话 / 跳舞 / 表演时不抢）。</summary>
    public bool IdleRandomGestureEnabled { get; set; } = true;

    /// <summary>待机随机小动作的基础间隔（秒，默认 22；实际每次在 0.55~1.6 倍之间随机）。</summary>
    public double IdleRandomGestureIntervalSeconds { get; set; } = 22.0;

    /// <summary>强制触发一次待机随机小动作（跳过倒计时；供自检 / 调试；条件不满足时返回 false）。</summary>
    internal bool TryTriggerIdleRandomGesture() => TryPlayRandomIdleDemo(mocapFace: false);

    /// <summary>把待机随机小动作倒计时清零（自检用，下一次 Tick 立刻进入判定）。</summary>
    internal void ResetIdleRandomGestureTimer() => _idleRandomGestureTimer = 0;

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

        // 7) 待机 / 眼睛跟随 / 身体晃动控制器 + 动捕参数表 + 受管参数表
        _eyeBlinkParameterIds = ResolveEyeBlinkIds(asset);
        _lipSyncParameterIds = asset.LipSyncParameterIds.ToArray();
        _idle = new IdleBehavior(_model, _eyeBlinkParameterIds);
        _eye = new EyeTrackingController(_model);
        _bodySway = new BodySwayController(_model);
        ResolveMocapParameters(_model);
        ResolveMusicParameters(_model);
        ResolveSpeechParameters(_model);
        CreateActionPlayers(_model);
        BuildManagedParameterGroups();

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

    /// <summary>
    /// 预计算「受管参数」分组（加载时一次）：每个子系统只在它开启时才每帧把这些参数恢复默认，
    /// 关掉后就不再复位，用户在「参数 / 面部测试」里手动拖的值才能保留。
    /// </summary>
    private void BuildManagedParameterGroups()
    {
        var model = _model!;

        var expression = new HashSet<int>();
        var physics = new HashSet<int>();
        var idle = new HashSet<int>();
        var blink = new HashSet<int>();
        var eye = new HashSet<int>();
        var mocap = new HashSet<int>();
        var sway = new HashSet<int>();

        void Add(HashSet<int> group, string id)
        {
            var parameter = model.FindParameter(id);
            if (parameter is not null) group.Add(parameter.Index);
        }

        // 表情参数：只有表情正在生效时才复位（没有表情时用户手调表情开关参数也能保留）
        foreach (var data in _expressionDataByPath.Values)
        {
            foreach (var parameter in data.Parameters) Add(expression, parameter.Id);
        }

        // 物理输出：只在 PhysicsEnabled 时接管
        if (_physics is not null)
        {
            foreach (var output in _physics.Outputs) Add(physics, output.DestinationId);
        }

        // 待机：呼吸（IdleEnabled）、眨眼（AutoBlink 且没被动捕接管时）
        Add(idle, "ParamBreath");
        foreach (var id in _eyeBlinkParameterIds) Add(blink, id);

        // 鼠标眼控：只在 EyeTrackingEnabled 时接管
        foreach (var id in EyeTrackingParameterIds) Add(eye, id);

        // 摄像头动捕：只在 MocapEnabled 时接管
        foreach (var id in MocapParameterIds) Add(mocap, id);

        // 身体晃动：只在 SwayEnabled / 演示计时中接管
        foreach (var id in BodySwayController.ManagedParameterIds) Add(sway, id);

        _expressionManaged = ToSortedArray(expression);
        _physicsManaged = ToSortedArray(physics);
        _idleManaged = ToSortedArray(idle);
        _blinkManaged = ToSortedArray(blink);
        _eyeManaged = ToSortedArray(eye);
        _mocapManaged = ToSortedArray(mocap);
        _swayManaged = ToSortedArray(sway);
    }

    /// <summary>HashSet → 排序后的下标数组（只在加载时调用）。</summary>
    private static int[] ToSortedArray(HashSet<int> set)
    {
        var indices = new int[set.Count];
        set.CopyTo(indices);
        Array.Sort(indices);
        return indices;
    }

    /// <summary>把一组参数恢复默认（0 分配；顺序按下标，和 Core 里参数数组无关）。</summary>
    private static void ResetParametersToDefault(CubismParameter[] parameters, int[] indices)
    {
        for (var i = 0; i < indices.Length; i++)
        {
            var parameter = parameters[indices[i]];
            parameter.Value = parameter.DefaultValue;
        }
    }

    /// <summary>
    /// 复位「上一帧动作写过的参数」（各个正在播的动作曲线的并集）。
    /// 这样待机 IDLE 只会复位它自己那 21 个参数，用户手调嘴 / 脸等其它参数不会被动作列表里别的文件拖累。
    /// </summary>
    private void ResetMotionTouchedParameters(CubismParameter[] parameters)
    {
        var player = _motionPlayer;
        var count = player.TouchedParameterCount;
        for (var i = 0; i < count; i++)
        {
            var parameter = parameters[player.GetTouchedParameterIndex(i)];
            parameter.Value = parameter.DefaultValue;
        }
    }

    /// <summary>
    /// 子系统开关从「开 → 关」的那一帧，把对应参数组恢复默认一次，避免模型停在奇怪的姿势里；
    /// 之后不再复位这些参数，用户手动拖的值就能保留（这是「面部测试」能用的关键）。
    /// </summary>
    private void HandleManagedGroupTransitions(bool blinkActive, bool swayActive, bool expressionActive)
    {
        var parameters = _model!.Parameters;
        var physicsEnabled = PhysicsEnabled;
        var idleEnabled = IdleEnabled;
        var eyeTrackingEnabled = EyeTrackingEnabled;
        var mocapEnabled = MocapEnabled;

        if (!_managedGroupsInitialized)
        {
            // 第一帧只记录状态，不做任何复位（避免加载完立刻把手动 / 配置状态冲掉）
            _managedGroupsInitialized = true;
        }
        else
        {
            if (_prevPhysicsEnabled && !physicsEnabled) ResetParametersToDefault(parameters, _physicsManaged);
            if (_prevIdleEnabled && !idleEnabled) ResetParametersToDefault(parameters, _idleManaged);
            if (_prevBlinkActive && !blinkActive) ResetParametersToDefault(parameters, _blinkManaged);
            if (_prevEyeTrackingEnabled && !eyeTrackingEnabled) ResetParametersToDefault(parameters, _eyeManaged);
            if (_prevMocapEnabled && !mocapEnabled) ResetParametersToDefault(parameters, _mocapManaged);
            if (_prevSwayActive && !swayActive) ResetParametersToDefault(parameters, _swayManaged);
            if (_prevExpressionActive && !expressionActive) ResetParametersToDefault(parameters, _expressionManaged);
        }

        _prevPhysicsEnabled = physicsEnabled;
        _prevIdleEnabled = idleEnabled;
        _prevBlinkActive = blinkActive;
        _prevEyeTrackingEnabled = eyeTrackingEnabled;
        _prevMocapEnabled = mocapEnabled;
        _prevSwayActive = swayActive;
        _prevExpressionActive = expressionActive;
    }

    /// <summary>解析音乐响应要写的参数（有就缓存引用，没有就跳过）。</summary>
    private void ResolveMusicParameters(CubismModel model)
    {
        _musicMouthOpenY = model.FindParameter("ParamMouthOpenY");
        _musicMouthForm = model.FindParameter("ParamMouthForm");
        _musicJawOpen = model.FindParameter("JawOpen");
        _musicParamAngleY = model.FindParameter("ParamAngleY");
    }

    /// <summary>
    /// 音乐响应：有正在播放的数据且开启时，写跟唱嘴型 + 可选轻微点头。
    /// <para>「身体律动」不再写模型参数（不扭身体），而是驱动 <see cref="BodyLeanDegrees"/> 的加速 / 加大幅度，由渲染层整身轻倾。</para>
    /// <para>嘴型优先级：动捕有脸 &gt; 音乐（动捕在驱动嘴时音乐不抢）。</para>
    /// <para>参数只在「这一帧真的写」的时候才记录到 touched 列表，下一帧开头复位；不写时完全不动，手动值保留。</para>
    /// </summary>
    private void ApplyMusicResponse(bool mocapFace)
    {
        _musicTouchedCount = 0;

        var provider = _music;
        var snapshot = provider?.Latest;
        var age = snapshot is null ? double.MaxValue : Environment.TickCount64 - snapshot.TimestampMs;
        var playing = MusicEnabled && snapshot is not null && snapshot.IsPlaying && age <= MusicDataTimeoutMs;
        _musicPlaying = playing;

        if (!playing)
        {
            _musicKind = MusicContentKind.Silent;
            SetMusicStatus(0);
            return;
        }

        // 有效类型：强制模式优先，否则用分析器结果
        var data = snapshot!;
        var vocal = MusicMode switch
        {
            MusicVocalMode.ForceVocal => true,
            MusicVocalMode.ForceInstrumental => false,
            _ => data.Kind == MusicContentKind.VocalSong,
        };

        _musicKind = data.Kind;
        SetMusicStatus(data.Kind switch
        {
            MusicContentKind.VocalSong => 3,
            MusicContentKind.Instrumental => 2,
            _ => 1,
        });

        // ---- 1) 跟唱嘴型（动捕有脸时让位给动捕；语音说话时音乐闭嘴） ----
        if (vocal && !mocapFace && !_speechActive)
        {
            var lipSync = (float)Math.Clamp(MusicLipSyncStrength, 0.0, 1.5);
            var mouthOpen = Math.Clamp((float)data.MouthOpen, 0.0f, 1.0f) * lipSync;
            SetMusicParameter(_musicMouthOpenY, mouthOpen);
            SetMusicParameter(_musicMouthForm, (float)data.MouthForm * 0.6f * lipSync);
            SetMusicParameter(_musicJawOpen, mouthOpen * 0.5f);
        }

        var phase = data.BeatPhase;
        if (phase < 0) phase -= Math.Floor(phase);
        var swayStrength = (float)Math.Clamp(MusicSwayStrength, 0.0, 1.5);

        // ---- 2) 轻微点头（动捕 / 鼠标眼控占着头部时不抢） ----
        // 侧向身体律动改由整身轻倾（BodyLeanDegrees）表现，这里不再写 ParamBodyAngleX 等参数。
        if (MusicHeadBobEnabled && !mocapFace && !EyeTrackingEnabled)
        {
            SetMusicParameter(_musicParamAngleY, (float)(-Math.Sin(2.0 * Math.PI * phase) * 2.0 * swayStrength));
        }
    }

    /// <summary>
    /// 整身以脚为支点的左右轻倾（渲染层按 pivot=脚底 旋转 canvas，这里只算角度）。
    /// <para>常态：4 秒周期、±0.6°；音乐在播且 <see cref="MusicSwayEnabled"/> 时：平滑加快到 2.6 秒、幅度 1.0°×<see cref="MusicSwayStrength"/>。</para>
    /// <para>关闭（<see cref="BodyLeanEnabled"/>=false）时幅度平滑回 0；相位连续推进，周期 / 幅度都用一阶平滑，避免跳变。</para>
    /// </summary>
    private void UpdateBodyLean(double deltaSeconds)
    {
        var musicActive = MusicEnabled && _musicPlaying && MusicSwayEnabled;
        var danceActive = _dance?.ActiveMode is not null;
        var fastActive = musicActive || danceActive;
        var targetPeriod = fastActive ? BodyLeanMusicPeriodSeconds : BodyLeanIdlePeriodSeconds;
        var targetAmplitude = fastActive
            ? BodyLeanMusicAmplitudeDegrees * (musicActive ? Math.Clamp(MusicSwayStrength, 0.0, 1.5) : 1.0)
            : BodyLeanIdleAmplitudeDegrees;
        if (!BodyLeanEnabled) targetAmplitude = 0.0;

        var smoothing = 1.0 - Math.Exp(-deltaSeconds / BodyLeanSmoothingSeconds);
        _bodyLeanPeriod += (targetPeriod - _bodyLeanPeriod) * smoothing;
        _bodyLeanAmplitude += (targetAmplitude - _bodyLeanAmplitude) * smoothing;
        if (_bodyLeanPeriod < 0.5) _bodyLeanPeriod = 0.5;

        // 相位连续推进：周期变化只改变推进速度，相位本身不跳
        _bodyLeanPhase += deltaSeconds / _bodyLeanPeriod;
        if (_bodyLeanPhase >= 1.0) _bodyLeanPhase -= Math.Floor(_bodyLeanPhase);
        if (_bodyLeanPhase < 0) _bodyLeanPhase += 1.0;

        BodyLeanDegrees = (float)(Math.Sin(2.0 * Math.PI * _bodyLeanPhase) * _bodyLeanAmplitude);
    }

    // ---------- 动作系统：手势 / 表演 / 舞蹈 / 语音口型 / 待机随机小动作 ----------

    /// <summary>加载时创建三个演示播放器（手势 / 表演 / 舞蹈）；动作数据查找用运行时自己的解析表。</summary>
    private void CreateActionPlayers(CubismModel model)
    {
        _gesture = new GesturePlayer(model, FindMotionEntryByName, FindMotionData, PlayActionMotion, StopActionMotion);
        _performance = new PerformancePlayer(model, StartGestureForPerformance, StopGestureForPerformance);
        _dance = new DanceController(model);
    }

    private MotionEntry? FindMotionEntryByName(string name)
    {
        var motions = _motions;
        for (var i = 0; i < motions.Length; i++)
        {
            if (string.Equals(motions[i].Name, name, StringComparison.Ordinal)) return motions[i];
        }

        return null;
    }

    private Motion3Data? FindMotionData(MotionEntry entry)
        => _motionDataByPath.TryGetValue(entry.FilePath, out var data) ? data : null;

    private void PlayActionMotion(MotionEntry entry, Motion3Data data, double fadeSeconds)
    {
        var model = _model;
        if (model is null) return;
        _motionPlayer.Play(model, entry, data, loop: false, fadeSeconds);
    }

    private void StopActionMotion(double fadeSeconds) => _motionPlayer.Stop(fadeSeconds);

    /// <summary>表演里触发的 gesture（不打断表演本身）。</summary>
    private void StartGestureForPerformance(string id) => _gesture?.Play(id, stopMotionFirst: false);

    private void StopGestureForPerformance() => _gesture?.Stop();

    /// <summary>推进手势 / 表演 / 舞蹈（在动作更新之后，覆盖 IDLE 的手臂曲线；物理求值之前）。</summary>
    private void UpdateActionPlayers(double deltaSeconds, bool mocapFace)
    {
        var allowTalk = !mocapFace && !_speechActive;
        _performance?.Update(deltaSeconds, allowTalk);
        _gesture?.Update(deltaSeconds);
        _dance?.Update(deltaSeconds);
    }

    /// <summary>解析语音口型参数。</summary>
    private void ResolveSpeechParameters(CubismModel model)
    {
        _speechMouthOpenY = model.FindParameter("ParamMouthOpenY");
        _speechMouthForm = model.FindParameter("ParamMouthForm");
        _speechJawOpen = model.FindParameter("JawOpen");
    }

    /// <summary>
    /// 语音口型：优先级「动捕有脸 &gt; 语音 &gt; 音乐」。语音在说话且没有动捕脸时写嘴，
    /// 让音乐跟唱在语音时闭嘴（见 <see cref="ApplyMusicResponse"/>）。
    /// </summary>
    private void ApplySpeechMouth(bool mocapFace)
    {
        _speechTouchedCount = 0;
        if (!_speechActive || mocapFace) return;
        var open = Math.Clamp(_speechMouthOpen, 0f, 1f);
        SetSpeechParameter(_speechMouthOpenY, open);
        SetSpeechParameter(_speechMouthForm, 0.15f);
        SetSpeechParameter(_speechJawOpen, open * 0.5f);
    }

    private void SetSpeechParameter(CubismParameter? parameter, float value)
    {
        if (parameter is null) return;
        parameter.Value = value;
        if (_speechTouchedCount >= _speechTouchedIndices.Length)
        {
            Array.Resize(ref _speechTouchedIndices, _speechTouchedIndices.Length * 2);
        }

        _speechTouchedIndices[_speechTouchedCount++] = parameter.Index;
    }

    /// <summary>复位上一帧语音写过的参数。</summary>
    private void ResetSpeechTouchedParameters(CubismParameter[] parameters)
    {
        for (var i = 0; i < _speechTouchedCount; i++)
        {
            var parameter = parameters[_speechTouchedIndices[i]];
            parameter.Value = parameter.DefaultValue;
        }

        _speechTouchedCount = 0;
    }

    /// <summary>待机随机小动作：倒计时 + 条件判定（播动作 / 音乐 / 说话 / 跳舞 / 表演时不抢）。</summary>
    private void UpdateIdleRandomGesture(double deltaSeconds, bool mocapFace)
    {
        if (!IdleRandomGestureEnabled)
        {
            _idleRandomGestureTimer = 0;
            _idleRandomWasBusy = false;
            return;
        }

        if (IsIdleRandomBusy(mocapFace))
        {
            // 忙碌期间不推进触发；恢复后至少冷却 3 秒，避免刚演完 / 刚说完立刻蹦一个小动作
            _idleRandomWasBusy = true;
            return;
        }

        if (_idleRandomWasBusy)
        {
            _idleRandomWasBusy = false;
            if (_idleRandomGestureTimer <= 3.0) _idleRandomGestureTimer = 3.0;
        }

        if (_idleRandomGestureTimer <= 0) _idleRandomGestureTimer = NextIdleRandomInterval();
        _idleRandomGestureTimer -= deltaSeconds;
        if (_idleRandomGestureTimer > 0) return;
        if (TryPlayRandomIdleDemo(mocapFace)) return;
        _idleRandomGestureTimer = 3.0; // 条件不满足：3 秒后再试
    }

    /// <summary>待机小动作的「忙碌」判定：有动捕 / 音乐 / 语音 / 演示 / 用户动作都算忙。</summary>
    private bool IsIdleRandomBusy(bool mocapFace)
    {
        if (mocapFace || _speechActive) return true;
        if (MusicEnabled && _musicPlaying) return true;
        if (_gesture?.HasActive == true || _performance?.HasActive == true || _dance?.HasActive == true) return true;
        if (_motionPlayer.HasActive && CurrentMotion?.IsIdleCandidate != true) return true;
        return false;
    }

    private bool CanPlayIdleRandomDemo(bool mocapFace)
    {
        if (IsIdleRandomBusy(mocapFace)) return false;
        return true;
    }

    private bool TryPlayRandomIdleDemo(bool mocapFace)
    {
        if (!CanPlayIdleRandomDemo(mocapFace)) return false;
        var gestureIds = _gesture?.Ids;
        var performanceIds = _performance?.Ids;
        var gestureCount = gestureIds?.Count ?? 0;
        var performanceCount = performanceIds?.Count ?? 0;
        var total = gestureCount + performanceCount;
        if (total == 0) return false;

        var pick = _idleRandomRng.Next(total);
        var played = pick < gestureCount
            ? PlayGesture(gestureIds![pick])
            : PlayPerformance(performanceIds![pick - gestureCount]);
        if (!played) return false;
        _idleRandomGestureCount++;
        _idleRandomGestureTimer = NextIdleRandomInterval();
        return true;
    }

    /// <summary>下一次待机小动作的间隔：基础间隔 × 0.55~1.6（默认 22s → 约 12~35s）。</summary>
    private double NextIdleRandomInterval()
    {
        var baseInterval = Math.Clamp(IdleRandomGestureIntervalSeconds, 4.0, 600.0);
        var factor = 0.55 + (_idleRandomRng.NextDouble() * 1.05);
        return baseInterval * factor;
    }

    /// <summary>写一个音乐响应参数并记录（下一帧开头按记录复位，保证不写时不污染手动值）。</summary>
    private void SetMusicParameter(CubismParameter? parameter, float value)
    {
        if (parameter is null) return;
        parameter.Value = value;
        if (_musicTouchedCount >= _musicTouchedIndices.Length)
        {
            Array.Resize(ref _musicTouchedIndices, _musicTouchedIndices.Length * 2);
        }

        _musicTouchedIndices[_musicTouchedCount++] = parameter.Index;
    }

    /// <summary>复位上一帧音乐写过的参数。</summary>
    private void ResetMusicTouchedParameters(CubismParameter[] parameters)
    {
        for (var i = 0; i < _musicTouchedCount; i++)
        {
            var parameter = parameters[_musicTouchedIndices[i]];
            parameter.Value = parameter.DefaultValue;
        }

        _musicTouchedCount = 0;
    }

    /// <summary>刷新音乐状态文案（不变时复用字符串，避免每帧分配）。</summary>
    private void SetMusicStatus(int kind)
    {
        if (_musicStatusKind == kind) return;
        _musicStatusKind = kind;
        _musicStatusText = kind switch
        {
            3 => MusicStatusVocal,
            2 => MusicStatusInstrumental,
            1 => MusicStatusAnalyzing,
            _ => MusicStatusSilent,
        };
    }

    /// <summary>解析动捕要写的参数（有就缓存引用，没有就跳过；本模型全都有）。</summary>
    private void ResolveMocapParameters(CubismModel model)
    {
        _mocapParamAngleX = model.FindParameter("ParamAngleX");
        _mocapParamAngleY = model.FindParameter("ParamAngleY");
        _mocapParamAngleZ = model.FindParameter("ParamAngleZ");
        _mocapAngleX = model.FindParameter("AngleX");
        _mocapAngleY = model.FindParameter("AngleY");
        _mocapAngleZ = model.FindParameter("AngleZ");
        _mocapEyeBallX = model.FindParameter("ParamEyeBallX");
        _mocapEyeBallY = model.FindParameter("ParamEyeBallY");
        _mocapEyeLOpen = model.FindParameter("ParamEyeLOpen");
        _mocapEyeROpen = model.FindParameter("ParamEyeROpen");
        _mocapMouthOpenY = model.FindParameter("ParamMouthOpenY");
        _mocapBodyAngleX = model.FindParameter("ParamBodyAngleX");
        _mocapPositionX = model.FindParameter("PositionX");
    }

    /// <summary>
    /// 读一帧动捕结果并刷新状态（给 UI 的 HasFace / Age / StatusText）。
    /// 返回 true 表示这一帧应该用动捕驱动模型（启用 + 有脸 + 结果未过期）。
    /// </summary>
    private bool UpdateMocapState(out MocapTrackingResult? result)
    {
        result = null;
        var provider = _mocap;
        if (!MocapEnabled || provider is null)
        {
            _mocapHasFace = false;
            _mocapLastFaceAgeMs = -1;
            SetMocapStatus(0);
            return false;
        }

        var latest = provider.Latest;
        if (latest is null)
        {
            _mocapHasFace = false;
            _mocapLastFaceAgeMs = -1;
            SetMocapStatus(0);
            return false;
        }

        var age = Environment.TickCount64 - latest.TimestampMs;
        _mocapLastFaceAgeMs = age;
        if (latest.HasFace && age <= MocapFaceTimeoutMs)
        {
            _mocapHasFace = true;
            SetMocapStatus(1);
            result = latest;
            return true;
        }

        _mocapHasFace = false;
        SetMocapStatus(2);
        return false;
    }

    /// <summary>刷新状态文案（只在状态变化时换字符串引用，避免每帧分配）。</summary>
    private void SetMocapStatus(int kind)
    {
        if (_mocapStatusKind == kind) return;
        _mocapStatusKind = kind;
        _mocapStatusText = kind switch
        {
            1 => MocapStatusTracking,
            2 => MocapStatusNoFace,
            _ => MocapStatusNoData,
        };
    }

    /// <summary>
    /// 把一帧动捕结果写进参数。
    /// <para>本模型实测：<c>Angle*</c> 是物理输入（Setting 1~3 变成 <c>ParamAngle*</c>），
    /// <c>ParamAngle*</c> 直接驱动头部变形，所以两套都写；身体用 <c>ParamBodyAngleX</c> + <c>PositionX</c> 温和跟随。</para>
    /// </summary>
    private void ApplyMocapFace(MocapTrackingResult result)
    {
        var headStrength = (float)Math.Clamp(MocapHeadStrength, 0.0, 1.5);
        var eyeStrength = (float)Math.Clamp(MocapEyeStrength, 0.0, 1.5);
        var mouthStrength = (float)Math.Clamp(MocapMouthStrength, 0.0, 1.5);

        if (MocapHeadEnabled)
        {
            var yaw = (float)result.Yaw * headStrength;
            var pitch = (float)result.Pitch * headStrength;
            var roll = (float)result.Roll * headStrength;

            SetParameter(_mocapParamAngleX, yaw);
            SetParameter(_mocapParamAngleY, pitch);
            SetParameter(_mocapParamAngleZ, roll);
            SetParameter(_mocapAngleX, yaw);
            SetParameter(_mocapAngleY, pitch);
            SetParameter(_mocapAngleZ, roll);

            // 身体温和跟随头部（Yaw × 比例），物理 Setting 4 会把它变成 ParamBodyAngleX
            var bodyX = yaw * MocapBodyFollowRatio;
            SetParameter(_mocapBodyAngleX, bodyX);
            SetParameter(_mocapPositionX, bodyX);
        }

        // 眼珠：由转头 / 点头方向近似，强度由 MocapEyeStrength 控制
        SetParameter(_mocapEyeBallX, Math.Clamp((float)result.PupilX * eyeStrength, -1.0f, 1.0f));
        SetParameter(_mocapEyeBallY, Math.Clamp((float)result.PupilY * eyeStrength, -1.0f, 1.0f));

        if (MocapBlinkEnabled)
        {
            // 参数范围 0~1.5、默认 1；FaceExpressionAnalyzer 给的开合度就是 0~1，直接写
            SetParameter(_mocapEyeLOpen, (float)result.EyeOpenLeft);
            SetParameter(_mocapEyeROpen, (float)result.EyeOpenRight);
        }

        if (MocapMouthEnabled)
        {
            SetParameter(_mocapMouthOpenY, (float)result.MouthOpen * mouthStrength);
        }
    }

    private static void SetParameter(CubismParameter? parameter, float value)
    {
        if (parameter is not null) parameter.Value = value; // 写入时已自动 clamp 到 min~max
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

        // 1) 受管参数：动作只复位「上一帧动作实际写过的参数」；表情只在生效时复位；
        //    子系统只在「开启」时复位——关掉后不再复位，用户在参数 / 面部测试里手动拖的值才能保留。
        ResetMotionTouchedParameters(parameters);
        ResetMusicTouchedParameters(parameters);
        ResetSpeechTouchedParameters(parameters);
        _gesture?.ResetTouched(parameters);
        _performance?.ResetTouched(parameters);
        _dance?.ResetTouched(parameters);
        var expressionActive = _expressionManager.HasActive;
        if (expressionActive) ResetParametersToDefault(parameters, _expressionManaged);
        if (PhysicsEnabled) ResetParametersToDefault(parameters, _physicsManaged);
        if (IdleEnabled) ResetParametersToDefault(parameters, _idleManaged);
        if (EyeTrackingEnabled) ResetParametersToDefault(parameters, _eyeManaged);
        if (MocapEnabled) ResetParametersToDefault(parameters, _mocapManaged);
        var swayActive = SwayEnabled || _swayDemoRemaining > 0;
        if (swayActive) ResetParametersToDefault(parameters, _swayManaged);

        // 1.1) 摄像头动捕状态（决定这一帧是否用动捕驱动 / 是否让自动眨眼接管）
        var mocapFace = UpdateMocapState(out var mocapResult);
        var blinkActive = AutoBlink && !(mocapFace && MocapBlinkEnabled);
        if (blinkActive) ResetParametersToDefault(parameters, _blinkManaged);

        // 1.2) 子系统从「开」切到「关」的那一帧恢复一次默认（之后不再碰，让手动值保留）
        HandleManagedGroupTransitions(blinkActive, swayActive, expressionActive);

        // 2) 待机行为（眨眼 / 呼吸 / 轻摇）：动捕接管眨眼时不让自动眨眼再写
        var idleEnabled = IdleEnabled;
        if (_idle is not null)
        {
            _idle.Update(_time, deltaSeconds, blinkActive, idleEnabled, idleEnabled);
        }

        // 3) 动捕有脸 → 写动捕参数（此时跳过鼠标眼控，避免两边抢同一批参数）；没脸 → 鼠标眼控 / 待机摇摆
        if (mocapFace && mocapResult is not null)
        {
            ApplyMocapFace(mocapResult);
        }
        else
        {
            var eye = _eye;
            if (eye is not null)
            {
                eye.Smoothing = EyeTrackingSmoothing;
                eye.HeadStrength = EyeTrackingHeadStrength;
                eye.EyeStrength = EyeTrackingEyeStrength;
                eye.BodyStrength = EyeTrackingBodyStrength;
                eye.HorizontalGain = EyeTrackingHorizontalGain;
                eye.UpGain = EyeTrackingUpGain;
                eye.DownGain = EyeTrackingDownGain;
                eye.VerticalOffset = EyeTrackingVerticalOffset;

                if (idleEnabled || EyeTrackingEnabled)
                {
                    eye.Update(deltaSeconds, EyeTrackingEnabled,
                        idleEnabled ? _idle?.SwayAngleZ ?? 0f : 0f,
                        idleEnabled ? _idle?.SwayBodyAngleZ ?? 0f : 0f);
                }
            }
        }

        // 3.5) 语音口型（优先级：动捕有脸 > 语音 > 音乐；语音时音乐跟唱闭嘴）
        ApplySpeechMouth(mocapFace);

        // 4) 身体晃动测试（在动捕之后、动作之前）；动捕有脸时晃动不做头部补偿，让头跟脸
        UpdateBodySway(deltaSeconds, mocapFace);

        // 4.5) 音乐跟唱 / 律动（在手动晃动之后；动捕有脸时嘴让位；侧向律动由整身轻倾表现）
        ApplyMusicResponse(mocapFace);

        // 4.6) 整身以脚为支点的左右轻倾：常态 4s/±0.6°，音乐在播平滑加速到 2.6s/±1.0°×SwayStrength
        UpdateBodyLean(deltaSeconds);

        // 5) 待机动作（IdleEnabled 且当前没有动作在播时循环播放）
        if (idleEnabled && IdleMotion is { } idleMotion && !_motionPlayer.HasActive &&
            _motionDataByPath.TryGetValue(idleMotion.FilePath, out var idleData))
        {
            _motionPlayer.Play(model, idleMotion, idleData, loop: true);
        }

        _motionPlayer.Update(model, deltaSeconds, _eyeBlinkParameterIds, _lipSyncParameterIds);

        // 5.5) 手势 / 时间线表演 / 舞蹈：写在动作之后，覆盖 IDLE 的手臂曲线（物理之前）
        UpdateActionPlayers(deltaSeconds, mocapFace);

        _expressionManager.Update(model, deltaSeconds);

        // 5.6) 待机随机小动作：没有动捕 / 音乐 / 语音 / 舞蹈 / 表演 / 用户动作时，按随机间隔播一个
        UpdateIdleRandomGesture(deltaSeconds, mocapFace);

        // 物理：在参数都算完之后、Core 求值之前跑（改的是参数值）
        if (PhysicsEnabled && _physics is not null)
        {
            _physics.Evaluate(deltaSeconds, (float)Math.Clamp(PhysicsStrength, 0.0, 1.0));
        }

        model.Update();
        _lastTickMilliseconds = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        FrameUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>身体晃动测试：限时演示倒计时 + 把运行时属性同步给控制器并执行。</summary>
    /// <param name="mocapActive">动捕这一帧是否在驱动头部；是的话晃动不做头部补偿（头优先跟脸）。</param>
    private void UpdateBodySway(double deltaSeconds, bool mocapActive)
    {
        if (_swayDemoRemaining > 0)
        {
            _swayDemoRemaining -= deltaSeconds;
            if (_swayDemoRemaining < 0) _swayDemoRemaining = 0;
        }

        var sway = _bodySway;
        if (sway is null) return;

        if (!SwayEnabled && _swayDemoRemaining <= 0)
        {
            sway.ClearApplied();
            return;
        }

        sway.Mode = SwayMode;
        sway.AmplitudeDegrees = SwayAmplitudeDegrees;
        sway.SpeedHz = SwaySpeedHz;
        // 动捕有脸时头部由动捕驱动：晃动不做头部反向补偿（否则会覆盖动捕写的 ParamAngle*/Angle*）
        sway.HeadFollow = mocapActive ? 0.0 : SwayHeadFollow;
        sway.IncludeHips = SwayIncludeHips;
        sway.Apply(_model!, deltaSeconds);
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

    /// <summary>把所有参数 / 部件恢复默认（表情 / 动作 / 晃动测试也停掉）。</summary>
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
        _bodySway?.Reset();
        _swayDemoRemaining = 0;
        _musicTouchedCount = 0;
        _gesture?.StopImmediate();
        _performance?.Stop();
        _dance?.StopImmediate();
        _speechTouchedCount = 0;
        _idleRandomGestureTimer = 0;
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
        _bodySway = null;
        _mocap = null;
        _mocapParamAngleX = null;
        _mocapParamAngleY = null;
        _mocapParamAngleZ = null;
        _mocapAngleX = null;
        _mocapAngleY = null;
        _mocapAngleZ = null;
        _mocapEyeBallX = null;
        _mocapEyeBallY = null;
        _mocapEyeLOpen = null;
        _mocapEyeROpen = null;
        _mocapMouthOpenY = null;
        _mocapBodyAngleX = null;
        _mocapPositionX = null;
        _music = null;
        _musicMouthOpenY = null;
        _musicMouthForm = null;
        _musicJawOpen = null;
        _musicParamAngleY = null;
        _musicTouchedCount = 0;
        _musicKind = MusicContentKind.Silent;
        _musicStatusKind = -1;
        _musicStatusText = MusicStatusSilent;
        _swayDemoRemaining = 0;
        _mocapHasFace = false;
        _mocapLastFaceAgeMs = -1;
        _mocapStatusKind = -1;
        _mocapStatusText = MocapStatusNoData;
        _motionDataByPath.Clear();
        _expressionDataByPath.Clear();
        _motionPlayer.Clear();
        _expressionManager.ClearImmediate();
        _gesture = null;
        _performance = null;
        _dance = null;
        _speechMouthOpenY = null;
        _speechMouthForm = null;
        _speechJawOpen = null;
        _speechTouchedCount = 0;
        _speechActive = false;
        _speechMouthOpen = 0;
        _idleRandomGestureTimer = 0;
        _motions = Array.Empty<MotionEntry>();
        _expressions = Array.Empty<ExpressionEntry>();
        _parameterBindings = Array.Empty<ParameterBinding>();
        _partBindings = Array.Empty<PartBinding>();
        _eyeBlinkParameterIds = Array.Empty<string>();
        _lipSyncParameterIds = Array.Empty<string>();
        _expressionManaged = Array.Empty<int>();
        _physicsManaged = Array.Empty<int>();
        _idleManaged = Array.Empty<int>();
        _blinkManaged = Array.Empty<int>();
        _eyeManaged = Array.Empty<int>();
        _mocapManaged = Array.Empty<int>();
        _swayManaged = Array.Empty<int>();
        _managedGroupsInitialized = false;
    }

    private void ReportProgress(double progress) => _options.Progress?.Invoke(Math.Clamp(progress, 0.0, 1.0));
}
