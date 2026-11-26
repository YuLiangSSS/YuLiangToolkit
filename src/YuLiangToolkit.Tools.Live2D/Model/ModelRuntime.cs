using System.Diagnostics;
using YuLiangToolkit.Tools.Live2D.Audio;
using YuLiangToolkit.Tools.Live2D.Interop;
using YuLiangToolkit.Tools.Live2D.Mocap;
using YuLiangToolkit.Tools.Live2D.Model.Control;
using YuLiangToolkit.Tools.Live2D.Play;
using YuLiangToolkit.Tools.Live2D.Rendering;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 模型运行时（工具页与桌宠共用）：加载资源 → 每帧 <see cref="Tick"/>（位姿合成 + 物理）→ <see cref="RenderFrame"/> 出图。
///
/// <para><b>加载顺序</b>（耗时大头是 5 张纹理）：moc3 → 模型 → cdi3 → 动作 / 表情解析 → 物理 → 纹理 → 渲染器。
/// 可以在后台线程调用 <see cref="Load"/>，加载完后再在 UI 线程 <see cref="Tick"/> / <see cref="RenderFrame"/>。</para>
///
/// <para><b>操控层 = 位姿合成器</b>（<see cref="PoseCompositor"/>）：每个会写参数的子系统是一个「层」，
/// 按登记顺序（= 优先级）逐层求值、按权重混进合成结果，最后一次性写回模型：
/// 手动值 → 待机呼吸 / 眨眼 → 鼠标眼控 / 摄像头动捕 → 身体晃动测试 → 音乐 / 语音口型 / 情绪鼓嘴 →
/// 待机动作 → 用户动作 → 舞蹈 → 表演 → 手势 → 待机小动作 → 鼠标互动 → 表情 →
/// 戳脸 / 害羞扭动 / 观察 → 投喂 →（写回 Core）→ 物理 → 摇尾巴 → 固定参数 → Core <c>Update()</c>。
/// 层与层的交接只有「权重」一种机制：任何动作收尾都拿它最后一帧的姿势交叉淡化回下一层，
/// 没有「帧首复位成默认值」这个中间状态（旧实现收回动作抽搐的根源）。</para>
///
/// <para>加载完成前 <see cref="Model"/> / <see cref="Renderer"/> 会抛 <see cref="InvalidOperationException"/>；先判 <see cref="IsLoaded"/>。</para>
/// </summary>
public sealed class ModelRuntime : IDisposable
{
    /// <summary>动捕有脸的有效期（毫秒）：超过这个时间没新结果就当没脸，恢复鼠标 / 待机。</summary>
    private const double MocapFaceTimeoutMs = 600.0;

    /// <summary>动捕时身体跟随头部的温和比例（Yaw × 该比例 → 身体左右）。</summary>
    private const float MocapBodyFollowRatio = 0.35f;

    /// <summary>
    /// 内置模型的眼珠 Y 参数在原 moc 里只到 1，实测白眼 / 向上看时黑眼珠
    /// 还没贴到上眼皮。运行时把上限扩到 2.5，让「翻白眼」更像真的翻上去；
    /// 超出部分由 Cubism Core 按参数值写网格，不改 moc / 纹理。
    /// </summary>
    private const float EyeBallYExtendedMaximum = 2.5f;

    /// <summary>动捕状态文案（不变时复用同一个字符串，避免每帧分配）。</summary>
    private const string MocapStatusNoData = "无数据";
    private const string MocapStatusTracking = "正在跟踪";
    private const string MocapStatusNoFace = "未检测到人脸";

    // ---------- 位姿合成（操控层） ----------
    //
    // 两个动作混合器：_idleMotionPlayer 只循环播待机动作（一直在最底下垫着），_motionPlayer 播用户点播的动作。
    // 旧实现把两者塞进同一个混合器、靠「没动作在播时才启动待机」切换，手势 enter / exit 期间还得特判压制待机；
    // 现在待机动作是一个常驻层，手势 / 用户动作是它上面的层，收尾时自然露出下面的待机。
    private readonly MotionPlayer _motionPlayer = new();
    private readonly MotionPlayer _idleMotionPlayer = new();
    private readonly ExpressionManager _expressionManager = new();
    private PoseCompositor? _pose;

    // 位姿层（登记顺序 = 优先级，见类注释）
    private PoseStage? _idleAutoStage;
    private PoseStage? _eyeStage;
    private PoseStage? _mocapStage;
    private PoseStage? _swayStage;
    private PoseStage? _musicStage;
    private PoseStage? _speechStage;
    private PoseStage? _moodStage;
    private PoseStage? _idleMotionStage;
    private PoseStage? _userMotionStage;
    private PoseStage? _danceStage;
    private PoseStage? _performanceStage;
    private PoseStage? _gestureStage;
    private PoseStage? _petIdleStage;
    private PoseStage? _interactionStage;
    private PoseStage? _expressionStage;
    private PoseStage? _pokeStage;
    private PoseStage? _shyStage;
    private PoseStage? _observationStage;
    private PoseStage? _feedingStage;

    /// <summary>实测无效的参数下标（改了画面一点不变；各层对它们的写入丢弃，UI 打「无效」标记）。</summary>
    private HashSet<int> _deadParameterIndices = new();
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
    private readonly Dictionary<string, (ExpressionEntry Entry, double Until)> _temporaryExpressions =
        new(StringComparer.OrdinalIgnoreCase);
    private ParameterBinding[] _parameterBindings = Array.Empty<ParameterBinding>();
    private PartBinding[] _partBindings = Array.Empty<PartBinding>();
    private string[] _eyeBlinkParameterIds = Array.Empty<string>();
    private string[] _lipSyncParameterIds = Array.Empty<string>();

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
    private bool _speechActive;
    private float _speechMouthOpen;

    // ---------- AI 聊天情绪鼓嘴（生气 / 闹别扭时鼓脸颊 + 撅嘴；由会话按 AI 回复的情绪写入） ----------

    private CubismParameter? _moodCheekPuff;
    private CubismParameter? _moodCheekPuffL;
    private CubismParameter? _moodCheekPuffR;
    private CubismParameter? _moodPucker;
    private float _moodPuffTarget;
    private float _moodPuffCurrent;

    // ---------- 投喂 / 吃饭：凑过去张嘴 → 嚼嚼嚼 → 伪吞咽（由会话按投喂层坐标写入） ----------

    private readonly FeedingController _feeding = new();
    private CubismParameter? _feedAngleX;
    private CubismParameter? _feedAngleY;
    private CubismParameter? _feedBodyAngleX;
    private CubismParameter? _feedEyeBallX;
    private CubismParameter? _feedEyeBallY;
    private CubismParameter? _feedMouthOpenY;
    private CubismParameter? _feedMouthForm;
    private CubismParameter? _feedJawOpen;

    /// <summary>待机随机小动作的倒计时（秒；到 0 且条件允许时播一个随机手势 / 表演）。</summary>
    private double _idleRandomGestureTimer;

    /// <summary>固定种子的随机数（自检可重复；只用于挑待机小动作）。</summary>
    private readonly Random _idleRandomRng = new(20250301);

    private int _idleRandomGestureCount;

    /// <summary>
    /// 待机随机手势摆好后保持的秒数（不含抬手 / 淡入；到点自动播作者退出动作还原）。
    /// 狗尾草这类「拿出来一直举着」的手势以前会卡住，现在 2.5 秒后自己收回去。
    /// </summary>
    internal const double IdleRandomGestureHoldSeconds = 2.5;

    /// <summary>待机随机小动作已触发次数（自检用）。</summary>
    internal int IdleRandomTriggerCount => _idleRandomGestureCount;

    /// <summary>上一帧是否处于「忙碌」状态（动捕 / 音乐 / 语音 / 演示 / 用户动作）；用于恢复待机后先冷却。</summary>
    private bool _idleRandomWasBusy;

    // ---------- 桌宠鼠标互动（参考 FULASKA/-live2d-） ----------

    private InteractionController? _interaction;
    private PetIdleActions? _petIdleActions;

    /// <summary>当前手势 / 表演 / 舞蹈是不是「待机随机」自动挑的（互动来了要让位）。</summary>
    private bool _idleDemoActive;

    private bool _interactionVoiceEnabled = true;
    private bool _petIdleActionsEnabled = true;
    private string? _lastInteractionVoiceEvent;
    private string? _lastInteractionName;
    private long _interactionEventCount;

    // ---------- 害羞扭动（高好感互动用；写在表情之后、参数求值之前） ----------
    private CubismParameter? _wiggleHeadAngleZ;
    private CubismParameter? _wiggleBodyAngleZ;
    private double _wiggleRemainingSeconds;
    private double _wiggleDurationSeconds;
    private double _wiggleElapsedSeconds;

    // ---------- 观察用户（单眼瞪大 / 微眯 / 歪头 / 盯人；写在表情与扭动之后） ----------
    private CubismParameter? _obsEyeL;
    private CubismParameter? _obsEyeR;
    private CubismParameter? _obsSquintL;
    private CubismParameter? _obsSquintR;
    private CubismParameter? _obsEyeBallX;
    private CubismParameter? _obsEyeBallY;
    private CubismParameter? _obsAngleZ;
    private CubismParameter? _obsBodyAngleZ;
    private CubismParameter? _obsBrowLY;
    private CubismParameter? _obsBrowRY;
    private CubismParameter? _obsBrowLAngle;
    private CubismParameter? _obsBrowRAngle;
    private CubismParameter? _obsMouthForm;
    private string? _obsFacePose;
    private bool _obsEyeWideLeft;
    private bool _obsClosedEyeLeft;
    private double _obsRemainingSeconds;
    private double _obsDurationSeconds;
    private double _obsElapsedSeconds;
    private double _obsTiltDirection;
    private double _obsTiltDegrees;
    private double _obsBodyTwistDegrees;
    private float _obsGazeX;
    private float _obsGazeY;
    private bool _obsLookAtUser;
    private bool _obsOverrideEyeBallY;
    private float _obsEyeBallYTarget;
    private IReadOnlyList<ObservationBlinkPlan.Burst>? _obsBlinkBursts;

    // ---------- 摇尾巴（高好感触摸随机触发；物理求值之后叠加） ----------
    private CubismParameter?[] _tailRotationParams = Array.Empty<CubismParameter?>();
    private CubismParameter? _tailSwitch;
    private double _tailWagRemainingSeconds;
    private double _tailWagDurationSeconds;
    private double _tailWagElapsedSeconds;
    private double _tailWagAmplitudeDegrees;

    // ---------- 戳脸彩蛋：＞＜ + 微微笑 + 左右晃脸 ----------
    private CubismParameter? _pokeAngleX;
    private CubismParameter? _pokeMouthForm;
    private CubismParameter? _pokeMouthSmileL;
    private CubismParameter? _pokeMouthSmileR;
    private double _pokeReactionRemainingSeconds;
    private double _pokeReactionDurationSeconds;
    private double _pokeReactionElapsedSeconds;
    /// <summary>戳脸彩蛋（＞＜）的左右晃头角度：0.9Hz 摆头，太浅了看不出来（用户反馈），12° 左右刚好明显。</summary>
    private double _pokeSwayDegrees = 12;
    private float _pokeMouthFormTarget = 0.55f;
    private float _pokeMouthSmileTarget = 0.45f;

    /// <summary>互动语音事件（参数 = 语音事件 key：pat_head / face_cycle / pinch / tickle / protect / hand）。</summary>
    public event EventHandler<string>? InteractionHappened;

    /// <summary>
    /// 互动触发事件：**每次互动都会触发**（不受「互动语音」开关影响）。
    /// 会话用它做动作 / 表情预设（高好感摸头前倾脸红爱心眼、摸裙子害羞扭动…），语音只管说话。
    /// </summary>
    public event EventHandler<string>? InteractionTriggered;

    private double _time;
    private double _lastTickMilliseconds;

    // ===================== 用户固定参数（Override：面板「锁定」的参数每帧强制回填） =====================

    // 面部测试 / 身体测试 / 手臂卡「锁定」后，把用户调好的值存进这里；
    // 每帧在所有子系统（待机 / 眨眼 / 动作 / 表情 / 晃动 / 互动）写完之后、物理求值之前回填一次，
    // 物理求值 + 摇尾巴之后、Core 求值之前再回填一次——
    // 第二次是必须的：ParamAngleX 这类参数同时是物理输出（头部摆的钟摆写回），物理会把锁定值覆盖掉；
    // 锁定的身体角度照常带动物理（头发 / 裙子跟着固定姿势摆），锁定的脸参数盖过动作 / 小动作 / 物理输出。
    private readonly List<(CubismParameter Parameter, float Value, string Group)> _parameterOverrides = new();
    private RenderStats _lastRenderStats = RenderStats.Empty;

    private ModelRuntime(RuntimeLoadOptions options) => _options = options;

    // ===================== 用户固定参数（Override）对外 API =====================

    /// <summary>
    /// 固定某参数为指定值：每帧在所有子系统写完之后强制回填（盖过待机 / 眨眼 / 动作 / 表情 / 晃动 / 互动），
    /// 且在物理求值之前——锁定的身体角度会照常带动物理（头发 / 裙子跟着固定姿势摆）。
    /// <paramref name="groupId"/> 用于按模块批量解除（face / body / arms）。
    /// </summary>
    public void SetParameterOverride(string groupId, string parameterId, float value)
    {
        if (string.IsNullOrWhiteSpace(groupId) || _model is null) return;
        var parameter = _model.FindParameter(parameterId);
        if (parameter is null) return;
        SetParameterOverride(groupId, parameter, value);
    }

    /// <summary>同 <see cref="SetParameterOverride(string,string,float)"/>，直接给参数对象（避免重复查找）。</summary>
    public void SetParameterOverride(string groupId, CubismParameter parameter, float value)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var clamped = Math.Clamp(value, parameter.Minimum, parameter.Maximum);
        for (var i = 0; i < _parameterOverrides.Count; i++)
        {
            if (ReferenceEquals(_parameterOverrides[i].Parameter, parameter))
            {
                _parameterOverrides[i] = (parameter, clamped, groupId);
                return;
            }
        }

        _parameterOverrides.Add((parameter, clamped, groupId));
    }

    /// <summary>解除某个模块（face / body / arms）的全部固定参数。</summary>
    public void ClearParameterOverrides(string groupId)
        => _parameterOverrides.RemoveAll(o => string.Equals(o.Group, groupId, StringComparison.Ordinal));

    /// <summary>解除单个参数的固定（其余固定参数保留）。</summary>
    public void ClearParameterOverride(string groupId, string parameterId)
    {
        if (_model is null) return;
        var parameter = _model.FindParameter(parameterId);
        if (parameter is null) return;
        _parameterOverrides.RemoveAll(o =>
            string.Equals(o.Group, groupId, StringComparison.Ordinal) && ReferenceEquals(o.Parameter, parameter));
    }

    /// <summary>解除全部固定参数。</summary>
    public void ClearAllParameterOverrides() => _parameterOverrides.Clear();

    /// <summary>是否有固定参数（可按模块过滤）。</summary>
    public bool HasParameterOverrides(string? groupId = null)
        => groupId is null
            ? _parameterOverrides.Count > 0
            : _parameterOverrides.Any(o => string.Equals(o.Group, groupId, StringComparison.Ordinal));

    /// <summary>把全部固定参数写回模型参数（Tick 里在物理求值之前调用；返回写入条数）。</summary>
    public int ApplyParameterOverrides()
    {
        foreach (var (parameter, value, _) in _parameterOverrides)
        {
            parameter.Value = value;
        }

        return _parameterOverrides.Count;
    }

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

    /// <summary>
    /// 当前动作（界面「动作区」显示的那一个）：用户点播的动作优先，没有就报正在循环的待机动作；都没有为 null。
    /// </summary>
    public MotionEntry? CurrentMotion => _motionPlayer.Current?.Entry ?? _idleMotionPlayer.Current?.Entry;

    /// <summary>当前动作已播放时间（秒）。</summary>
    public double CurrentMotionTime => _motionPlayer.HasActive ? _motionPlayer.CurrentTime : _idleMotionPlayer.CurrentTime;

    /// <summary>当前动作是否在循环。</summary>
    public bool CurrentMotionLoop => (_motionPlayer.Current ?? _idleMotionPlayer.Current)?.Loop ?? false;

    /// <summary>当前层状态快照（自检 / 调试：层名、是否活动、权重、本帧写了几个参数）。</summary>
    internal IReadOnlyList<PoseStage> PoseStages => _pose?.Stages ?? Array.Empty<PoseStage>();

    /// <summary>
    /// 参数是否实测有效（改它的值画面会变）。无效 = 作者只留了个开关、模型里没有任何网格 / 变形器绑它
    /// （例如「翅膀」系列）；各层对它们的写入一律丢弃，面板里也会打「无效」标记。
    /// </summary>
    public bool IsParameterLive(string parameterId)
        => _model?.FindParameter(parameterId) is not { } parameter || !_deadParameterIndices.Contains(parameter.Index);

    /// <summary>实测无效的参数 Id 列表（自检 / UI 用）。</summary>
    public IReadOnlyList<string> DeadParameterIds => _parameterEffect?.DeadParameterIds ?? Array.Empty<string>();

    /// <summary>最近一次参数有效性探测的完整结果（有效 / 无效参数、环境复测、耗时；自检 / UI 用）。</summary>
    public ParameterEffectResult? ParameterEffect => _parameterEffect;

    private ParameterEffectResult? _parameterEffect;

    /// <summary>
    /// 各层对无效参数的写入累计被丢弃了多少次（自检用）：内置动作表全部跑一遍后这个数必须是 0——
    /// 动作里只要写了实测无效的参数（比如翅膀），这里就会露出来。
    /// </summary>
    public long DroppedLayerWrites => _pose?.DroppedWrites ?? 0;

    /// <summary>按参数统计的丢弃写入报告（参数 id × 次数，按次数降序取前 12 个；诊断手写动作表残留用）。</summary>
    public string DeadWriteReport()
    {
        var pose = _pose;
        if (pose is null || pose.DroppedByParameter.Count == 0) return "（无丢弃）";
        var parameters = _model!.Parameters;
        return string.Join(", ", pose.DroppedByParameter
            .OrderByDescending(pair => pair.Value)
            .Take(12)
            .Select(pair => $"{(pair.Key < parameters.Length ? parameters[pair.Key].Id : pair.Key.ToString())}×{pair.Value}"));
    }

    /// <summary>立刻播一个待机小动作（不传名字随机；自检逐个验收动作表用）。</summary>
    public bool TryPlayPetIdleAction(string? name = null) => _petIdleActions?.TryTrigger(name) ?? false;

    /// <summary>一张层状态快照（层名 / 权重 / 本帧是否活动 / 本帧写了几个有效参数；自检 / 调试用）。</summary>
    public readonly record struct PoseStageInfo(string Name, float Weight, bool Wrote, int WrittenCount);

    /// <summary>当前所有位姿层的状态（顺序 = 优先级）。</summary>
    public IReadOnlyList<PoseStageInfo> GetPoseStageSnapshot()
        => _pose is null
            ? Array.Empty<PoseStageInfo>()
            : _pose.Stages.Select(stage => new PoseStageInfo(stage.Name, stage.Weight, stage.Wrote, stage.WrittenCount)).ToArray();

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

    /// <summary>
    /// 动作 / 互动 / 观察时暂停「鼠标跟随」（默认开）：这些模块自己会驱动头 / 眼，
    /// 不让鼠标跟随再抢一遍，否则视线会被鼠标带偏（用户反馈）。
    /// </summary>
    public bool EyeTrackingPauseOnAction { get; set; } = true;

    /// <summary>暂停策略（纯函数，自检用）：总开关开 + 任一动作模块激活 → 暂停鼠标跟随。</summary>
    public static bool ShouldPauseEyeTracking(bool pauseOnAction, bool observing, bool gestureActive,
        bool performanceActive, bool danceActive, bool interacting, bool pokeReacting = false)
        => pauseOnAction && (observing || gestureActive || performanceActive || danceActive || interacting || pokeReacting);

    /// <summary>最近一帧鼠标跟随是否因动作模块被暂停（调试 / 自检观察用）。</summary>
    public bool EyeTrackingPaused { get; private set; }

    /// <summary>最近一帧音乐内容类型（没在播 / 关闭时为 Silent；数据不足为 Unknown）。</summary>
    public MusicContentKind MusicKind => _musicKind;


    /// <summary>音乐状态文案："静音" / "识别中" / "纯音乐" / "带歌词"（不变时复用字符串）。</summary>
    public string MusicStatusText => _musicStatusText;

    // ---------- 动作系统：手势 / 表演 / 舞蹈 / 语音口型 / 待机随机小动作 ----------

    /// <summary>所有手势 id（3 个作者原生手势 + 5 个参数姿势，中文名）。</summary>
    public IReadOnlyList<string> GestureIds => _gesture?.Ids ?? Array.Empty<string>();

    /// <summary>当前手势 id（没有为 null）。</summary>
    public string? ActiveGesture => _gesture?.ActiveId;

    /// <summary>
    /// 播一个手势（未知 id 返回 false）。
    /// <para><b>同一手势重复触发</b>（互动控制器 + 会话异步回调会各来一次）：只刷新保持计时，不重播 enter。</para>
    /// <para>不同手势 / 首次触发：手势是比用户动作 / 表演 / 舞蹈更高的层，旧的演示直接停掉
    /// （各自按层的淡出时间交回，不会一帧跳回），避免多层同时抢手臂。</para>
    /// <paramref name="autoRestoreAfterHoldSeconds"/> 大于 0 时，摆好姿势后保持该秒数自动播退出动作还原。
    /// </summary>
    public bool PlayGesture(string id, double autoRestoreAfterHoldSeconds = 0)
    {
        if (_gesture is null) return false;
        if (!_gesture.IsKnownId(id)) return false;

        // 同一手势已经在播：只刷新保持计时（手势播放器内部处理），别打断正在播的 enter 动作
        if (_gesture.IsActive(id)) return _gesture.Play(id, autoRestoreAfterHoldSeconds);

        // 换手势：表演 / 舞蹈 / 用户动作让位（各层按自己的淡出时间交回）
        _performance?.Stop();
        _dance?.StopImmediate();
        StopMotion(0.12);
        var played = _gesture.Play(id, autoRestoreAfterHoldSeconds);
        if (played) _idleDemoActive = false;
        return played;
    }

    /// <summary>停止手势（原生手势从当前保持姿势播作者退出动作；参数姿势平滑淡出）。</summary>
    public void StopGesture()
    {
        _gesture?.Stop();
        _idleDemoActive = false;
    }

    // ---------- 害羞扭动（高好感互动 / 被摸裙子等） ----------

    /// <summary>是不是正在害羞扭动（UI / 自检观察用）。</summary>
    public bool IsShyWiggling => _wiggleRemainingSeconds > 0;

    /// <summary>
    /// 做一段「扭扭捏捏」：身体左右轻轻扭、头部延迟一点点跟着扭（头比身体慢约 0.45 弧度），
    /// 幅度先淡入再淡出，约 2 秒后自动回到正常姿势。
    /// 只写 <c>ParamBodyAngleZ</c> / <c>ParamAngleZ</c>；参数不存在的模型会安静跳过。
    /// </summary>
    /// <param name="seconds">持续秒数（0.3 ~ 6，默认 2.2）。</param>
    public void StartShyWiggle(double seconds = 2.2)
    {
        _wiggleDurationSeconds = Math.Clamp(seconds, 0.3, 6.0);
        _wiggleRemainingSeconds = _wiggleDurationSeconds;
        _wiggleElapsedSeconds = 0;
    }

    /// <summary>立刻停止扭动（本层拿最后一帧姿势 0.15 秒淡出回正）。</summary>
    public void StopShyWiggle() => _wiggleRemainingSeconds = 0;

    // ---------- 摇尾巴 ----------

    /// <summary>是不是正在摇尾巴（UI / 自检观察用）。</summary>
    public bool IsTailWagging => _tailWagRemainingSeconds > 0;

    /// <summary>调试 / 验收用：6 段尾巴当前角度（物理+摇尾巴叠加后的值，逗号分隔）。</summary>
    public string TailWagDebugText => string.Join(",",
        _tailRotationParams.Select(p => p is null ? "-" : p.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// 尾巴当前是否露着（没被「尾巴开关」装饰藏起来）。
    /// 读 <c>Clothes5</c>：等于默认值 = 正常露尾；被装饰开关改掉（默认值 - 1）= 藏起来，不摇。
    /// </summary>
    public bool IsTailVisible
        => _tailSwitch is null || Math.Abs(_tailSwitch.Value - _tailSwitch.DefaultValue) < 0.2f;

    /// <summary>
    /// 摇一段尾巴：在物理求值之后给 6 段尾巴叠加正弦摆动（根部小、尾尖大、逐节延迟）。
    /// </summary>
    /// <param name="seconds">持续秒数（0.5~6，默认 2.6）。</param>
    /// <param name="amplitudeDegrees">根部幅度（度，3~16，默认 8；尾尖约 2 倍）。</param>
    public void StartTailWag(double seconds = 2.6, double amplitudeDegrees = 8)
    {
        _tailWagDurationSeconds = Math.Clamp(seconds, 0.5, 6.0);
        _tailWagRemainingSeconds = _tailWagDurationSeconds;
        _tailWagElapsedSeconds = 0;
        _tailWagAmplitudeDegrees = Math.Clamp(amplitudeDegrees, 3, 16);
    }

    /// <summary>停止摇尾巴（物理下一帧会自己回正；物理关着时这里写回默认）。</summary>
    public void StopTailWag()
    {
        _tailWagRemainingSeconds = 0;
        if (_pose is null || PhysicsEnabled) return;
        // 尾巴角度是物理输出 + 摇尾巴直通叠加的：物理关着时摇尾巴直接写 Core，这里也要直通写回默认
        _pose.RunBypass(() =>
        {
            foreach (var parameter in _tailRotationParams)
            {
                if (parameter is not null) parameter.Value = parameter.DefaultValue;
            }
        });
    }

    // ---------- 戳脸彩蛋：微微笑 + 左右晃脸 ----------

    /// <summary>是不是正在做戳脸彩蛋（＞＜ + 微微笑 + 左右晃脸）。</summary>
    public bool IsPokeReacting => _pokeReactionRemainingSeconds > 0;

    /// <summary>
    /// 临时「嘴形 + 晃脸」反应（戳脸彩蛋 / 摸胸反应共用）：
    /// 嘴形 <c>ParamMouthForm</c> → <paramref name="mouthFormTarget"/>、<c>MouthSmile_L/R</c> → <paramref name="mouthSmileTarget"/>；
    /// 头 <c>ParamAngleX</c> 以 0.9Hz、±<paramref name="swayDegrees"/> 左右晃（传 0 = 不晃），首尾 0.25s 淡入淡出。
    /// </summary>
    /// <param name="seconds">持续秒数（0.8~6，默认 2.5）。</param>
    /// <param name="swayDegrees">左右晃脸角度（0 = 只改嘴形；默认 7）。</param>
    /// <param name="mouthFormTarget">嘴形目标（笑 +0.55 / 生气 -0.45 / 黑脸 -0.7）。</param>
    /// <param name="mouthSmileTarget">嘴角上扬目标（笑 +0.45 / 生气 -0.2 / 无 0）。</param>
    public void StartPokeReaction(double seconds = 2.5, double swayDegrees = 7,
        float mouthFormTarget = 0.55f, float mouthSmileTarget = 0.45f)
    {
        _pokeReactionDurationSeconds = Math.Clamp(seconds, 0.8, 6.0);
        _pokeReactionRemainingSeconds = _pokeReactionDurationSeconds;
        _pokeReactionElapsedSeconds = 0;
        _pokeSwayDegrees = Math.Clamp(swayDegrees, 0, 20);
        _pokeMouthFormTarget = mouthFormTarget;
        _pokeMouthSmileTarget = mouthSmileTarget;
    }

    /// <summary>立刻结束戳脸彩蛋（本层拿最后一帧姿势 0.15 秒淡出回正）。</summary>
    public void StopPokeReaction() => _pokeReactionRemainingSeconds = 0;

    /// <summary>写戳脸彩蛋这一帧的输出：嘴形笑 + 头左右晃（在表情层之后；动捕有脸时不写，让位给真人脸）。</summary>
    private void WritePokeReaction()
    {
        if (_pokeReactionRemainingSeconds <= 0) return;

        var progress = _pokeReactionDurationSeconds <= 0
            ? 1.0
            : Math.Clamp(_pokeReactionElapsedSeconds / _pokeReactionDurationSeconds, 0.0, 1.0);
        var weight = (float)Math.Min(1.0, Math.Min(progress / 0.1, (1.0 - progress) / 0.1));
        if (weight < 0) weight = 0;

        var sway = (float)(Math.Sin(2 * Math.PI * 0.9 * _pokeReactionElapsedSeconds) * _pokeSwayDegrees) * weight;
        if (_pokeAngleX is not null) _pokeAngleX.Value = _pokeAngleX.DefaultValue + sway;

        WritePokeMouth(_pokeMouthForm, _pokeMouthFormTarget, weight);
        // MouthSmile_L/R 实测无效已移除：微微笑靠 ParamMouthForm 表现
    }

    private static void WritePokeMouth(CubismParameter? parameter, float target, float weight)
    {
        if (parameter is null) return;
        parameter.Value = parameter.DefaultValue + ((target - parameter.DefaultValue) * weight);
    }

    /// <summary>
    /// 推进限时反应的计时（戳脸 / 害羞扭动 / 观察）：不管这一帧层有没有在写都要走，
    /// 例如动捕有脸时戳脸彩蛋不写参数，但计时照样往下走。
    /// </summary>
    private void TickReactionTimers(double deltaSeconds)
    {
        if (_pokeReactionRemainingSeconds > 0)
        {
            _pokeReactionElapsedSeconds += deltaSeconds;
            _pokeReactionRemainingSeconds -= deltaSeconds;
            if (_pokeReactionRemainingSeconds < 0) _pokeReactionRemainingSeconds = 0;
        }

        if (_wiggleRemainingSeconds > 0)
        {
            _wiggleElapsedSeconds += deltaSeconds;
            _wiggleRemainingSeconds -= deltaSeconds;
            if (_wiggleRemainingSeconds < 0) _wiggleRemainingSeconds = 0;
        }

        if (_obsRemainingSeconds > 0)
        {
            _obsElapsedSeconds += deltaSeconds;
            _obsRemainingSeconds -= deltaSeconds;
            if (_obsRemainingSeconds < 0)
            {
                _obsRemainingSeconds = 0;
                _obsFacePose = null;
            }
        }
    }

    /// <summary>推进摇尾巴：在物理求值之后给尾巴 6 段角度参数叠加偏移。</summary>
    private void ApplyTailWag(double deltaSeconds)
    {
        if (_tailWagRemainingSeconds <= 0) return;
        if (!IsTailVisible)
        {
            // 尾巴被装饰藏起来了：不摇（留着计时也没意义，直接停）
            _tailWagRemainingSeconds = 0;
            return;
        }

        _tailWagElapsedSeconds += deltaSeconds;
        _tailWagRemainingSeconds -= deltaSeconds;
        for (var i = 0; i < _tailRotationParams.Length; i++)
        {
            var parameter = _tailRotationParams[i];
            if (parameter is null) continue;
            var baseValue = PhysicsEnabled ? parameter.Value : parameter.DefaultValue;
            parameter.Value = baseValue + (float)TailWag.Offset(
                _tailWagElapsedSeconds, _tailWagDurationSeconds, _tailWagAmplitudeDegrees, i);
        }

        if (_tailWagRemainingSeconds <= 0 && !PhysicsEnabled)
        {
            foreach (var parameter in _tailRotationParams)
            {
                if (parameter is not null) parameter.Value = parameter.DefaultValue;
            }
        }
    }

    /// <summary>写害羞扭动这一帧的输出：身体 1.1Hz 左右扭、头部滞后 0.45 弧度，幅度两端淡入淡出。</summary>
    private void WriteShyWiggle()
    {
        if (_wiggleRemainingSeconds <= 0) return;

        var progress = _wiggleDurationSeconds <= 0
            ? 1.0
            : Math.Clamp(_wiggleElapsedSeconds / _wiggleDurationSeconds, 0.0, 1.0);
        // 两端各用 20% 时间淡入 / 淡出，避免忽然开始扭、忽然定住
        var envelope = Math.Min(1.0, Math.Min(progress / 0.2, (1.0 - progress) / 0.2));
        var phase = 2.0 * Math.PI * 1.1 * _wiggleElapsedSeconds;
        var body = Math.Sin(phase) * 7.0 * envelope;
        var head = Math.Sin(phase - 0.45) * 4.5 * envelope;

        WriteWiggleParameter(_wiggleBodyAngleZ, (float)body);
        WriteWiggleParameter(_wiggleHeadAngleZ, (float)head);
    }

    private static void WriteWiggleParameter(CubismParameter? parameter, float offset)
    {
        if (parameter is null) return;
        parameter.Value = parameter.DefaultValue + offset;
    }

    // ---------- 观察用户：单眼瞪大 / 双眼微眯 / 白眼 / 不满 + 歪头 + 盯人 ----------

    /// <summary>是不是正在做「观察用户」的表情 / 姿势（UI / 自检观察用）。</summary>
    public bool IsObserving => _obsRemainingSeconds > 0;

    /// <summary>当前观察表情预设名（没在观察为 null）。</summary>
    public string? ObservationFacePose => _obsFacePose;

    /// <summary>
    /// 开始一段「观察用户」：
    /// <list type="bullet">
    ///   <item><paramref name="facePose"/>：单眼瞪大 / 双眼微眯 / 微眯偏睁 / 白眼 / 不满 / 好奇；</item>
    ///   <item><paramref name="tiltDirection"/>：歪头方向（-1 左 / +1 右）；<paramref name="tiltDegrees"/>：歪头角度；</item>
    ///   <item><paramref name="bodyTwistDegrees"/>：身体跟着微扭的角度（同方向，一般 2~4°）；</item>
    ///   <item><paramref name="gazeX"/>/<paramref name="gazeY"/>：用户方向（-1~1，正 = 右 / 上）；</item>
    ///   <item><paramref name="lookAtUser"/>：是否用眼球盯住用户方向；</item>
    ///   <item><paramref name="seconds"/>：持续秒数（0.5~8，到点平滑回正）。</item>
    /// </list>
    /// 只写眼睛 / 眉毛 / 嘴形 / 头 Z / 身体 Z / 眼球参数；模型缺参数会安静跳过。
    /// </summary>
    public void StartObservation(string? facePose, double seconds, double tiltDirection, double tiltDegrees,
        double bodyTwistDegrees, float gazeX, float gazeY, bool lookAtUser)
    {
        _obsFacePose = string.IsNullOrWhiteSpace(facePose) ? "好奇" : facePose.Trim();
        _obsDurationSeconds = Math.Clamp(seconds, 0.5, 8.0);
        _obsRemainingSeconds = _obsDurationSeconds;
        _obsElapsedSeconds = 0;
        _obsTiltDirection = Math.Sign(tiltDirection) == 0 ? 1 : Math.Sign(tiltDirection);
        _obsTiltDegrees = Math.Clamp(tiltDegrees, 0, 24);
        _obsBodyTwistDegrees = Math.Clamp(bodyTwistDegrees, 0, 12);
        _obsGazeX = Math.Clamp(gazeX, -1f, 1f);
        _obsGazeY = Math.Clamp(gazeY, -1f, 1f);
        _obsLookAtUser = lookAtUser;
        _obsEyeWideLeft = Random.Shared.Next(2) == 0;
        _obsClosedEyeLeft = Random.Shared.Next(2) == 0;
        _obsBlinkBursts = _obsFacePose == "单眼闭眨"
            ? ObservationBlinkPlan.Build(_obsDurationSeconds, Random.Shared)
            : null;
        var targets = ObservationFaceTargets.For(_obsFacePose);
        _obsOverrideEyeBallY = targets.OverrideEyeBallY;
        _obsEyeBallYTarget = targets.EyeBallY;
    }

    /// <summary>立刻停止观察（本层拿最后一帧姿势 0.15 秒淡出回正）。</summary>
    public void StopObservation()
    {
        _obsRemainingSeconds = 0;
        _obsFacePose = null;
    }

    /// <summary>写「观察用户」这一帧的输出：表情 + 歪头 + 身体微扭（计时由 <see cref="TickReactionTimers"/> 推进）。</summary>
    private void WriteObservation()
    {
        if (_obsRemainingSeconds <= 0) return;

        var progress = _obsDurationSeconds <= 0
            ? 1.0
            : Math.Clamp(_obsElapsedSeconds / _obsDurationSeconds, 0.0, 1.0);
        var weight = (float)Math.Min(1.0, Math.Min(progress / 0.18, (1.0 - progress) / 0.18));
        if (weight < 0) weight = 0;

        var targets = ObservationFaceTargets.For(_obsFacePose ?? "好奇");
        var eyeWide = 1.15f;
        var eyeNarrow = 0.42f;   // 小眼不再完全眯上：留一点缝，看起来是「一大一小」而不是单眼闭
        var eyeL = _obsFacePose == "单眼瞪大" && !_obsEyeWideLeft ? eyeNarrow : targets.EyeL;
        var eyeR = _obsFacePose == "单眼瞪大" && _obsEyeWideLeft ? eyeNarrow : targets.EyeR;
        if (_obsFacePose == "单眼瞪大")
        {
            if (_obsEyeWideLeft) eyeL = eyeWide; else eyeR = eyeWide;
        }
        else if (_obsFacePose == "单眼闭眨")
        {
            // 一只眼闭上、另一只睁着；睁着的那只按计划「连眨 1~3 下」（长动作做两组），别眨太快
            var open = 1.0f - (ObservationBlinkPlan.BlinkPulse(_obsElapsedSeconds, _obsBlinkBursts) * 0.95f);
            eyeL = _obsClosedEyeLeft ? 0.05f : open;
            eyeR = _obsClosedEyeLeft ? open : 0.05f;
        }

        WriteObservationParameter(_obsEyeL, eyeL, weight);
        WriteObservationParameter(_obsEyeR, eyeR, weight);
        WriteObservationParameter(_obsSquintL, targets.SquintL, weight);
        WriteObservationParameter(_obsSquintR, targets.SquintR, weight);
        WriteObservationParameter(_obsBrowLY, targets.BrowY, weight);
        WriteObservationParameter(_obsBrowRY, targets.BrowY, weight);
        WriteObservationParameter(_obsBrowLAngle, targets.BrowAngle, weight);
        WriteObservationParameter(_obsBrowRAngle, targets.BrowAngle, weight);
        WriteObservationParameter(_obsMouthForm, targets.Mouth, weight);

        if (_obsLookAtUser)
        {
            WriteObservationParameter(_obsEyeBallX, _obsGazeX * 0.9f, weight);
            WriteObservationParameter(_obsEyeBallY,
                _obsOverrideEyeBallY ? _obsEyeBallYTarget : _obsGazeY * 0.8f, weight);
        }

        // 歪头 + 身体同向微扭（头更大、身体更小，一起才像「歪着头打量」）
        if (_obsAngleZ is not null)
            _obsAngleZ.Value = _obsAngleZ.DefaultValue + (float)(_obsTiltDirection * _obsTiltDegrees) * weight;
        if (_obsBodyAngleZ is not null)
            _obsBodyAngleZ.Value = _obsBodyAngleZ.DefaultValue + (float)(_obsTiltDirection * _obsBodyTwistDegrees) * weight;
    }

    private static void WriteObservationParameter(CubismParameter? parameter, float target, float weight)
    {
        if (parameter is null) return;
        parameter.Value = parameter.DefaultValue + ((target - parameter.DefaultValue) * weight);
    }

    /// <summary>「单眼闭眨」的眨眼计划在 StartObservation 时生成，这里不再需要额外脉冲函数。</summary>

    /// <summary>观察表情预设的参数目标（眼睛开合 / 眯眼 / 眉毛 / 嘴形 / 眼球 Y）。</summary>
    private readonly record struct ObservationFaceTargets(
        float EyeL, float EyeR, float SquintL, float SquintR,
        float BrowY, float BrowAngle, float Mouth, float EyeBallY, bool OverrideEyeBallY)
    {
        public static ObservationFaceTargets For(string pose) => pose switch
        {
            // 一只眼瞪大、一只眼眯小（参考「拿放大镜看」的表情）；具体哪边大由 StartObservation 随机
            "单眼瞪大" => new(1.15f, 0.18f, 0.0f, 0.0f, 0.15f, -0.10f, 0.15f, 0, false),
            // 双眼微眯
            "双眼微眯" => new(0.55f, 0.55f, 0.35f, 0.35f, 0.15f, 0.05f, 0.15f, 0, false),
            // 高好感「好奇地观察」：微眯但比平时稍微睁大一点
            "微眯偏睁" => new(0.72f, 0.72f, 0.18f, 0.18f, 0.28f, 0.05f, 0.20f, 0, false),
            // 白眼 / 欠揍：眼珠翻到上眼睑附近（运行时 Y 上限 2.5），上眼睑压一点
            "白眼" => new(0.55f, 0.55f, 0.25f, 0.25f, 0.25f, 0.10f, -0.10f, 2.4f, true),
            // 单眼闭上、一只眼睁开（开合由 ApplyObservation 里的眨眼脉冲单独写）
            "单眼闭眨" => new(1.0f, 1.0f, 0.0f, 0.0f, 0.15f, 0.05f, 0.15f, 0, false),
            // 不满 / 嫌弃：眉头压低、嘴形下撇
            "不满" => new(0.62f, 0.62f, 0.15f, 0.15f, -0.30f, -0.55f, -0.70f, 0, false),
            // 好奇：眼睛正常、眉毛抬起
            _ => new(1.0f, 1.0f, 0.0f, 0.0f, 0.35f, 0.05f, 0.20f, 0, false),
        };
    }

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
        var played = _performance.Play(id);
        if (played) _idleDemoActive = false;
        return played;
    }

    /// <summary>停止当前表演。</summary>
    public void StopPerformance()
    {
        _performance?.Stop();
        _idleDemoActive = false;
    }

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
        var started = _dance.Start(mode, speed);
        if (started) _idleDemoActive = false;
        return started;
    }

    /// <summary>停止跳舞（0.25 秒权重淡出）。</summary>
    public void StopDance()
    {
        _dance?.Stop();
        _idleDemoActive = false;
    }

    /// <summary>停止所有演示动作（手势 / 表演 / 舞蹈 / 当前动作）。</summary>
    public void StopAllDemos()
    {
        _gesture?.StopImmediate();
        _performance?.Stop();
        _dance?.StopImmediate();
        StopMotion(0.2);
        _idleDemoActive = false;
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

    /// <summary>
    /// AI 聊天情绪鼓嘴（0~1）：生气 / 闹别扭 / 吃醋时鼓脸颊 + 轻微撅嘴，正常时 0。
    /// <para>由会话按 AI 回复的情绪写入，Tick 里平滑逼近；不写嘴部张开参数（说话口型优先级更高），
    /// 只写脸颊鼓起 + 轻微撅嘴。模型没有这些参数时自动跳过。</para>
    /// </summary>
    public float MoodPuffAmount
    {
        get => _moodPuffTarget;
        set => _moodPuffTarget = Math.Clamp(value, 0f, 1f);
    }

    // ---------- 投喂 / 吃饭（供会话驱动） ----------

    /// <summary>投喂控制器（凑过去 / 张嘴 / 嚼嚼嚼 / 伪吞咽；状态机 + 纯数学，自检可测）。</summary>
    public FeedingController Feeding => _feeding;

    /// <summary>是否正在吃（咀嚼 / 吞咽中）。</summary>
    public bool IsEating => _feeding.IsEating;

    /// <summary>
    /// 饭团靠近：让桌宠朝它偏头 / 身体前倾 / 张嘴。
    /// <paramref name="normalizedX"/> / <paramref name="normalizedY"/> 是相对桌宠中心的方向（-1~1），
    /// <paramref name="strength"/> 0~1（越近越大），<paramref name="mouthOpen"/> 0~1（越接近嘴张越大）。
    /// </summary>
    public void SetFeedFocus(float normalizedX, float normalizedY, float strength, float mouthOpen)
        => _feeding.SetFocus(normalizedX, normalizedY, strength, mouthOpen);

    /// <summary>饭团被扔远 / 收起了：取消凑近（平滑回平常）。</summary>
    public void ClearFeedFocus() => _feeding.ClearFocus();

    /// <summary>吃到饭团：开始「嚼嚼嚼（默认 1.4 秒）→ 伪吞咽（默认 0.65 秒）→ 平常」。</summary>
    public void StartEating(double chewSeconds = FeedingController.DefaultChewSeconds,
        double swallowSeconds = FeedingController.DefaultSwallowSeconds)
        => _feeding.StartEating(chewSeconds, swallowSeconds);

    /// <summary>待机随机小动作开关（默认开；播动作 / 音乐 / 说话 / 跳舞 / 表演时不抢）。</summary>
    public bool IdleRandomGestureEnabled { get; set; } = true;

    /// <summary>待机随机小动作的基础间隔（秒，默认 22；实际每次在 0.55~1.6 倍之间随机）。</summary>
    public double IdleRandomGestureIntervalSeconds { get; set; } = 22.0;

    /// <summary>
    /// 强制触发一次待机随机小动作（跳过倒计时；供自检 / 调试；条件不满足时返回 false）。
    /// <paramref name="forcedGestureId"/> 非空时强制挑指定手势（自检用来精确覆盖狗尾草自动还原路径）。
    /// </summary>
    internal bool TryTriggerIdleRandomGesture(string? forcedGestureId = null)
        => TryPlayRandomIdleDemo(mocapFace: false, forcedGestureId);

    /// <summary>把待机随机小动作倒计时清零（自检用，下一次 Tick 立刻进入判定）。</summary>
    internal void ResetIdleRandomGestureTimer() => _idleRandomGestureTimer = 0;

    // ---------- 桌宠鼠标互动（参考 FULASKA/-live2d-） ----------

    /// <summary>是否开启桌宠鼠标互动（配置 PetInteractionEnabled；关闭后 HitTest 返回 None、所有交互 no-op）。</summary>
    public bool InteractionEnabled
    {
        get => _interaction?.Enabled ?? false;
        set
        {
            if (_interaction is not null) _interaction.Enabled = value;
        }
    }

    /// <summary>互动时是否触发语音事件（配置 PetInteractionVoiceEnabled；UI 用它决定要不要读台词说话）。</summary>
    public bool InteractionVoiceEnabled
    {
        get => _interactionVoiceEnabled;
        set => _interactionVoiceEnabled = value;
    }

    /// <summary>拖动桌宠时是否带拖拽物理（配置 PetDragPhysicsEnabled；关闭时 ApplyDragImpulse no-op）。</summary>
    public bool DragPhysicsEnabled
    {
        get => _interaction?.DragPhysicsEnabled ?? false;
        set
        {
            if (_interaction is not null) _interaction.DragPhysicsEnabled = value;
        }
    }

    /// <summary>旧名兼容（= <see cref="DragPhysicsEnabled"/>）。</summary>
    public bool PetDragPhysicsEnabled
    {
        get => DragPhysicsEnabled;
        set => DragPhysicsEnabled = value;
    }

    /// <summary>待机小动作总开关（换脚 / 东张西望 / 圈圈眼 / 屑屑眼 / 封面wink / 变小卖萌 / 变小蹦迪等 59 个）。</summary>
    public bool PetIdleActionsEnabled
    {
        get => _petIdleActionsEnabled;
        set
        {
            _petIdleActionsEnabled = value;
            if (_petIdleActions is not null) _petIdleActions.Enabled = value;
        }
    }

    /// <summary>待机小动作基础间隔（秒，配置 PetIdleActionIntervalSeconds；实际每次在约 8~18s 随机）。</summary>
    public double PetIdleActionIntervalSeconds
    {
        get => _petIdleActions?.IntervalSeconds ?? 12.0;
        set
        {
            if (_petIdleActions is not null) _petIdleActions.IntervalSeconds = value;
        }
    }

    /// <summary>待机时是否随机换表情（爱心眼 / 脸红 / 钢盆等；配置 PetIdleExpressionEnabled）。</summary>
    public bool PetIdleExpressionEnabled
    {
        get => _petIdleActions is null || _petIdleActions.ExpressionEnabled;
        set
        {
            if (_petIdleActions is not null) _petIdleActions.ExpressionEnabled = value;
        }
    }

    /// <summary>是否启用全局快捷键（配置 PetHotkeysEnabled；只在 UI 层使用，Model 不读）。</summary>
    public bool PetHotkeysEnabled { get; set; } = true;

    /// <summary>当前待机小动作名（没有为 null）。</summary>
    public string? ActivePetIdleAction => _petIdleActions?.ActiveName;

    /// <summary>全部待机小动作名。</summary>
    public IReadOnlyList<string> PetIdleActionNames => _petIdleActions?.ActionNames ?? Array.Empty<string>();

    /// <summary>所有互动热区（不含 None）。</summary>
    public IReadOnlyList<InteractionRegion> InteractionRegionIds => _interaction?.RegionIds ?? Array.Empty<InteractionRegion>();

    /// <summary>所有互动热区（旧名兼容 = <see cref="InteractionRegionIds"/>）。</summary>
    public IReadOnlyList<InteractionRegion> InteractionRegions => InteractionRegionIds;

    /// <summary>当前按下的热区（没有按下 / 反应 = None）。</summary>
    public InteractionRegion ActiveInteractionRegion => _interaction?.ActiveRegion ?? InteractionRegion.None;

    /// <summary>是否有互动正在进行（按下 / 反应中 / 捏脸中）。</summary>
    public bool InteractionActive => _interaction?.IsInteracting == true;

    /// <summary>区域中文名。</summary>
    public string GetRegionDisplayName(InteractionRegion region) => InteractionController.GetRegionDisplayName(region);

    /// <summary>画布像素坐标命中哪个互动热区（关闭互动 / 拿不到几何时返回 None）。</summary>
    public InteractionRegion HitTest(float canvasX, float canvasY) => _interaction?.HitTest(canvasX, canvasY) ?? InteractionRegion.None;

    /// <summary>嘴部在画布上的近似中心（投喂判定用；拿不到脸返回 false）。</summary>
    public bool TryGetMouthCanvasCenter(out float canvasX, out float canvasY)
    {
        if (_interaction is not null) return _interaction.TryGetMouthCanvasCenter(out canvasX, out canvasY);
        canvasX = 0;
        canvasY = 0;
        return false;
    }

    /// <summary>嘴部判定半径（画布像素；投喂判定用）。</summary>
    public float MouthCanvasRadius => _interaction?.MouthCanvasRadius ?? 30f;

    /// <summary>旧名兼容。</summary>
    public InteractionRegion HitTestInteraction(float canvasX, float canvasY) => HitTest(canvasX, canvasY);

    /// <summary>某个热区的代表点（画布像素；供调试钩子 / 自检模拟点击）。</summary>
    public bool GetRegionCenter(InteractionRegion region, out float canvasX, out float canvasY)
    {
        if (_interaction is not null) return _interaction.GetRegionCenter(region, out canvasX, out canvasY);
        canvasX = 0;
        canvasY = 0;
        return false;
    }

    /// <summary>旧名兼容。</summary>
    public bool GetInteractionRegionCenter(InteractionRegion region, out float canvasX, out float canvasY)
        => GetRegionCenter(region, out canvasX, out canvasY);

    /// <summary>指针按下（UI 换算成画布像素后转发；返回 true = 命中热区，应捕获指针走互动）。</summary>
    public bool InteractionPointerDown(float canvasX, float canvasY) => _interaction?.PointerDown(canvasX, canvasY) ?? false;

    /// <summary>指针移动（抚摸 / 捏脸 / 拖动推进；返回是否有互动在进行）。</summary>
    public bool InteractionPointerMove(float canvasX, float canvasY) => _interaction?.PointerMove(canvasX, canvasY) ?? false;

    /// <summary>指针抬起（内部判定 tap / pet / drag 并触发反应）。</summary>
    public void InteractionPointerUp() => _interaction?.PointerUp();

    /// <summary>指针抬起（带最后坐标）。</summary>
    public void InteractionPointerUp(float canvasX, float canvasY) => _interaction?.PointerUp(canvasX, canvasY);

    /// <summary>旧名兼容：显式按指定区域按下（调试 / 自检用）。</summary>
    public void PressInteraction(InteractionRegion region, float canvasX, float canvasY) => _interaction?.Press(region, canvasX, canvasY);

    /// <summary>旧名兼容：拖动 / 抚摸推进。</summary>
    public void DragInteraction(float canvasX, float canvasY) => _interaction?.PointerMove(canvasX, canvasY);

    /// <summary>旧名兼容：抬起。</summary>
    public void ReleaseInteraction() => _interaction?.PointerUp();

    /// <summary>旧名兼容：抬起（带坐标）。</summary>
    public void ReleaseInteraction(float canvasX, float canvasY) => _interaction?.PointerUp(canvasX, canvasY);

    /// <summary>拖动桌宠时喂位移（像素），驱动头发 / 裙子甩动的物理冲量；约 0.6 秒衰减。</summary>
    public void ApplyDragImpulse(float dxPixels, float dyPixels) => _interaction?.ApplyDragImpulse(dxPixels, dyPixels);

    /// <summary>显式触发一次语义互动（调试钩子 / 菜单用）。</summary>
    public bool TriggerInteraction(InteractionRegion region, InteractionGesture gesture)
        => _interaction?.TriggerInteraction(region, gesture) ?? false;

    /// <summary>额头点击：常态 → 爱心眼 → 单片眼镜 / 黑框眼镜交替 → 常态。返回当前状态中文名。</summary>
    public string CycleFaceExpression() => _interaction?.CycleFaceExpression() ?? "常态";

    /// <summary>最近一次互动对应的语音事件 key（pat_head / face_cycle / pinch / tickle / protect / hand）。</summary>
    public string? LastInteractionVoiceEvent => _lastInteractionVoiceEvent;

    /// <summary>最近一次互动的中文名（摸头 / 额头表情 / 捏脸 / 挠痒 / 护住 / 竖食指 / 放下手 …）。</summary>
    public string? LastInteractionName => _lastInteractionName;

    /// <summary>
    /// 互动事件累计次数（每次摸头 / 捏脸 / 挠痒…都 +1；**不受「互动语音」开关影响**）。
    /// 会话的每日任务「摸摸头」靠它计数，关掉互动语音也能完成任务。
    /// </summary>
    public long InteractionEventCount => _interactionEventCount;

    // ---------- 桌宠互动 / 待机小动作：自检观察口 ----------

    internal InteractionController? InteractionForTest => _interaction;
    internal PetIdleActions? PetIdleActionsForTest => _petIdleActions;

    /// <summary>自检用：强制触发一个待机小动作。</summary>
    internal bool TriggerPetIdleActionForTest(string? name = null) => _petIdleActions?.TryTrigger(name) ?? false;

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
        // 只对内置「大肥鱼」应用眼珠 Y 扩展；自定义模型保留作者自己的参数范围，避免超出作者设计的变形范围。
        var isBuiltInModel = string.Equals(Path.GetFileName(asset.MocPath), "大肥鱼.moc3", StringComparison.OrdinalIgnoreCase);
        _moc = isBuiltInModel
            ? CubismMoc.LoadWithParameterMaximum(asset.MocPath, "ParamEyeBallY", EyeBallYExtendedMaximum)
            : CubismMoc.Load(asset.MocPath);
        log?.Invoke($"moc3 版本 {_moc.MocVersion}（{CubismMoc.DescribeMocVersion(_moc.MocVersion)}），" +
                    $"Core {CubismMoc.GetCoreVersionText()}，一致性 {( _moc.IsConsistent ? "通过" : "未通过")}");
        ReportProgress(0.08);
        cancellationToken.ThrowIfCancellationRequested();

        _model = CubismModel.Create(_moc);
        _parameterOverrides.Clear();   // 模型重建：旧参数对象全部失效，固定参数清空（UI 会按锁定状态重新套用）

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
        // 渲染后端日志：GPU（D3D11）优先，建不出来自动落回 CPU 软件光栅器（原因记 GpuError）
        var isD3DImage = _renderer.Surface is System.Windows.Interop.D3DImage;
        log?.Invoke(_renderer.IsGpu
            ? $"渲染层：GPU（D3D11，D3DImage{(isD3DImage ? "零拷贝" : "读回")}出图）"
            : $"渲染层：CPU 软件光栅器（{_renderer.GpuError}）");

        // 7) 待机 / 眼睛跟随 / 身体晃动控制器 + 动捕参数表 + 受管参数表
        _eyeBlinkParameterIds = ResolveEyeBlinkIds(asset);
        _lipSyncParameterIds = asset.LipSyncParameterIds.ToArray();
        _idle = new IdleBehavior(_model, _eyeBlinkParameterIds);
        _eye = new EyeTrackingController(_model);
        _bodySway = new BodySwayController(_model);
        ResolveMocapParameters(_model);
        ResolveMusicParameters(_model);
        ResolveSpeechParameters(_model);
        CreatePoseStages(_model);     // 先于各播放器：手势播放器要拿合成器开临时捕获
        RunParameterAudit(_model, log);   // 实测哪些参数改了值画面不变（各层对它们的写入丢弃）
        CreateActionPlayers(_model);
        CreateInteractionPlayers(_model);

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

    // ===================== 位姿合成：层登记（顺序 = 优先级） =====================

    /// <summary>
    /// 建位姿合成器并登记全部层。<b>登记顺序就是优先级</b>：先登记的先合成、会被后登记的盖住——
    /// 这是全工具唯一一处定义「谁盖过谁」的地方。各层只管把自己的输出写进捕获缓冲，交接全靠权重。
    /// </summary>
    private void CreatePoseStages(CubismModel model)
    {
        var pose = new PoseCompositor(model);
        _pose = pose;

        // 常驻驱动层（显式 Active：开关 / 有没有数据决定这一帧求不求值）。
        // 鼠标眼控 / 摄像头动捕「接管」和「让位」都是立刻生效：这两层是互斥的头部驱动，中间态没有意义。
        // 下面这些层都是我们手写的驱动（区别于作者的 motion / exp3 文件）：统一打上手写表标记，
        // 对实测无效参数的写入丢弃并计数（自检「动作表体检」用）。
        _idleAutoStage = pose.AddStage("待机呼吸 / 眨眼", fadeInSeconds: 0, fadeOutSeconds: 0.2);
        _idleAutoStage.DropsDeadWrites = true;
        _eyeStage = pose.AddStage("鼠标眼控", fadeInSeconds: 0, fadeOutSeconds: 0);
        _eyeStage.DropsDeadWrites = true;
        _mocapStage = pose.AddStage("摄像头动捕", fadeInSeconds: 0, fadeOutSeconds: 0);
        _mocapStage.DropsDeadWrites = true;
        _swayStage = pose.AddStage("身体晃动测试", fadeInSeconds: 0, fadeOutSeconds: 0);
        _swayStage.DropsDeadWrites = true;

        // 音乐层每帧都求值：状态文案 / 整身轻倾要用它算出来的「在不在播」，有没有输出另算
        _musicStage = pose.AddStage("音乐跟唱", fadeInSeconds: 0, fadeOutSeconds: 0, alwaysEvaluate: true);
        _musicStage.DropsDeadWrites = true;
        _speechStage = pose.AddStage("语音口型", fadeInSeconds: 0, fadeOutSeconds: 0);
        _speechStage.DropsDeadWrites = true;
        _moodStage = pose.AddStage("情绪鼓嘴", fadeInSeconds: 0, fadeOutSeconds: 0);
        _moodStage.DropsDeadWrites = true;

        // 动作层：待机动作一直垫在最底下；用户动作 / 手势 / 表演 / 舞蹈是它上面的层，收尾时自然露出待机。
        // 两个动作混合器自带逐实例交叉淡化，所以这两层不再叠一层淡出（0）。
        _idleMotionStage = pose.AddStage("待机动作", fadeInSeconds: 0, fadeOutSeconds: 0.3);
        _userMotionStage = pose.AddStage("用户动作", fadeInSeconds: 0, fadeOutSeconds: 0);

        // 动作层（每帧求值，活动与否看本帧有没有写参数）；收尾统一拿最后一帧姿势淡出（安全网）。
        // 舞蹈 / 表演 / 手势 / 待机小动作 / 鼠标互动是我们手写的动作表：对实测无效参数的写入丢弃并计数
        // （自检「动作表体检」用），作者的 motion 文件不受此约束。
        _danceStage = pose.AddStage("舞蹈", fadeInSeconds: 0, fadeOutSeconds: 0.25, alwaysEvaluate: true);
        _danceStage.DropsDeadWrites = true;
        _performanceStage = pose.AddStage("表演", fadeInSeconds: 0, fadeOutSeconds: 0.25, alwaysEvaluate: true);
        _performanceStage.DropsDeadWrites = true;
        _gestureStage = pose.AddStage("手势", fadeInSeconds: 0, fadeOutSeconds: 0.3, alwaysEvaluate: true);
        _gestureStage.DropsDeadWrites = true;
        _petIdleStage = pose.AddStage("待机小动作", fadeInSeconds: 0, fadeOutSeconds: 0.25, alwaysEvaluate: true);
        _petIdleStage.DropsDeadWrites = true;
        _interactionStage = pose.AddStage("鼠标互动", fadeInSeconds: 0, fadeOutSeconds: 0.2, alwaysEvaluate: true);
        _interactionStage.DropsDeadWrites = true;

        // 表情自己管淡入淡出（Add / Multiply / Overwrite 都基于「当前值」），层权重不参与
        _expressionStage = pose.AddStage("表情", fadeInSeconds: 0, fadeOutSeconds: 0);

        // 限时反应（首尾自带包络；被 Stop 打断时拿最后一帧 0.15 秒淡出）
        _pokeStage = pose.AddStage("戳脸彩蛋", fadeInSeconds: 0, fadeOutSeconds: 0.15);
        _pokeStage.DropsDeadWrites = true;
        _shyStage = pose.AddStage("害羞扭动", fadeInSeconds: 0, fadeOutSeconds: 0.15);
        _shyStage.DropsDeadWrites = true;
        _observationStage = pose.AddStage("观察用户", fadeInSeconds: 0, fadeOutSeconds: 0.15);
        _observationStage.DropsDeadWrites = true;
        _feedingStage = pose.AddStage("投喂", fadeInSeconds: 0, fadeOutSeconds: 0.2);
        _feedingStage.DropsDeadWrites = true;
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
    /// <para>嘴型优先级：动捕有脸 &gt; 语音 &gt; 音乐（动捕在驱动嘴时音乐不抢）。这一帧没东西可写 = 音乐层没有输出，
    /// 嘴型立刻交回下面各层（手动值 / 表情）。</para>
    /// </summary>
    private void ApplyMusicResponse(bool mocapFace)
    {
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

    /// <summary>
    /// 登记实测无效的参数（改了它们画面一点不变）：合成器丢弃各层对它们的写入，面板里打「无效」标记。
    /// 换模型 / 重新探测后整体替换。
    /// </summary>
    private void ApplyDeadParameters(ParameterEffectResult result)
    {
        _parameterEffect = result;
        var dead = new HashSet<int>();
        foreach (var id in result.DeadParameterIds)
        {
            var parameter = _model?.FindParameter(id);
            if (parameter is not null) dead.Add(parameter.Index);
        }

        _deadParameterIndices = dead;
        _pose?.IgnoreLayerWritesTo(dead.Count > 0 ? dead : null);
    }

    /// <summary>
    /// 参数有效性探测：默认姿势下逐个拉参数看画面变不变；无效的再放进「开关环境」复测。
    /// 环境两组：cdi3 里「开关」分组的参数各一个（换装 / 体型 / 手部切换这类开关）+ 每个表情文件的参数组合。
    /// 物理输入 / 输出参数按「先验有效」处理（它们不直接绑变形器、只在物理求值时影响画面，逐像素测不出来）。
    /// </summary>
    private void RunParameterAudit(CubismModel model, Action<string>? log)
    {
        try
        {
            // 先验有效：物理 rig 的全部输入（含多级链的中间参数）+ 输出
            var alwaysLive = new HashSet<int>();
            if (_physics is not null)
            {
                foreach (var input in _physics.Inputs)
                {
                    if (input.SourceParameterIndex >= 0) alwaysLive.Add(input.SourceParameterIndex);
                }

                foreach (var output in _physics.Outputs)
                {
                    if (output.DestinationParameterIndex >= 0) alwaysLive.Add(output.DestinationParameterIndex);
                }
            }

            var contexts = new List<IReadOnlyList<(string Id, float Value)>>(32);

            var switchIds = new List<string>();
            if (_display is not null)
            {
                foreach (var group in _display.ParameterGroups)
                {
                    if (!string.Equals(group.Name, "开关", StringComparison.Ordinal)) continue;
                    switchIds.AddRange(group.ParameterIds);
                }
            }

            if (switchIds.Count == 0)
            {
                // 没有 cdi3（自定义模型）：用通用开关名兜底
                switchIds.AddRange(Enumerable.Range(1, 13).Select(i => "key" + i));
                switchIds.AddRange(Enumerable.Range(1, 5).Select(i => "Clothes" + i));
                switchIds.AddRange(Enumerable.Range(1, 3).Select(i => "Movement" + i));
            }

            foreach (var id in switchIds)
            {
                var parameter = model.FindParameter(id);
                if (parameter is null) continue;
                var value = parameter.Maximum != parameter.DefaultValue ? parameter.Maximum : parameter.Minimum;
                if (value == parameter.DefaultValue) continue;
                contexts.Add(new[] { (id, value) });
            }

            foreach (var data in _expressionDataByPath.Values)
            {
                var context = new List<(string Id, float Value)>();
                foreach (var expressionParameter in data.Parameters)
                {
                    var parameter = model.FindParameter(expressionParameter.Id);
                    if (parameter is null) continue;
                    var value = Math.Clamp(expressionParameter.Value, parameter.Minimum, parameter.Maximum);
                    if (value == parameter.DefaultValue) continue;
                    context.Add((expressionParameter.Id, value));
                }

                if (context.Count > 0) contexts.Add(context);
            }

            ApplyDeadParameters(ParameterEffectAudit.Run(model, contexts, log, alwaysLive));
        }
        catch (Exception exception)
        {
            // 探测只是锦上添花：失败就当「全部有效」，不影响加载
            log?.Invoke($"参数有效性探测失败（按全部有效处理）：{exception.GetType().Name}: {exception.Message}");
            ApplyDeadParameters(new ParameterEffectResult(
                model.Parameters.Select(p => p.Id).ToArray(), Array.Empty<string>(), Array.Empty<string>(), 0, 0));
        }
    }

    /// <summary>加载时创建三个演示播放器（手势 / 表演 / 舞蹈）；动作数据查找用运行时自己的解析表。</summary>
    private void CreateActionPlayers(CubismModel model)
    {
        _gesture = new GesturePlayer(model, _pose!, FindMotionEntryByName, FindMotionData);
        _performance = new PerformancePlayer(model, StartGestureForPerformance, StopGestureForPerformance,
            FindExpressionByName, ApplyExpression, RemoveExpression);
        _dance = new DanceController(model);
    }

    /// <summary>加载时创建鼠标互动控制器 + 待机小动作调度器。</summary>
    private void CreateInteractionPlayers(CubismModel model)
    {
        _interaction = new InteractionController(
            model,
            _partBindings,
            FindExpressionByName,
            ApplyExpression,
            RemoveExpression,
            PlayInteractionGesture,
            StopInteractionGesture,
            GetActiveInteractionGesture,
            OnInteractionEvent,
            EnterInteractionMode);
        _petIdleActions = new PetIdleActions(model, FindExpressionByName, ApplyExpression, ClearExpressions)
        {
            Enabled = _petIdleActionsEnabled,
            CanApplyExpression = CanPetIdleUseExpression,
        };
    }

    private ExpressionEntry? FindExpressionByName(string name)
    {
        var expressions = _expressions;
        for (var i = 0; i < expressions.Length; i++)
        {
            if (string.Equals(expressions[i].Name, name, StringComparison.Ordinal)) return expressions[i];
        }

        for (var i = 0; i < expressions.Length; i++)
        {
            if (string.Equals(expressions[i].Name, name, StringComparison.OrdinalIgnoreCase)) return expressions[i];
        }

        return null;
    }

    /// <summary>
    /// 互动里播手势（不打断用户当前动作；互动开始已经处理过待机演示让位）。
    /// 最多保持 3 秒自动还原：点手切「竖起食指」这类操作即使没再点一次，也不会永远举着。
    /// </summary>
    private bool PlayInteractionGesture(string id) => _gesture?.Play(id, 3.0) ?? false;

    private void StopInteractionGesture() => _gesture?.Stop();

    private string? GetActiveInteractionGesture() => _gesture?.ActiveId;

    /// <summary>互动事件：记录最近一次的语音 key / 中文名，并按配置触发 <see cref="InteractionHappened"/>。</summary>
    private void OnInteractionEvent(string voiceEvent, string displayName)
    {
        _lastInteractionVoiceEvent = voiceEvent;
        _lastInteractionName = displayName;
        _interactionEventCount++;
        // 每次都通知（视觉 / 动作反馈）；语音事件仍然受「互动语音」开关控制。
        InteractionTriggered?.Invoke(this, voiceEvent);
        if (_interactionVoiceEnabled) InteractionHappened?.Invoke(this, voiceEvent);
    }

    /// <summary>
    /// 互动开始：如果当前演示是「待机随机」自动挑的，让位停掉；待机小动作也停掉并进入 3 秒冷却。
    /// 用户自己播的表演 / 舞蹈在 <see cref="IsInteractionBlocked"/> 里已经拒绝互动，不会走到这里。
    /// </summary>
    private void EnterInteractionMode()
    {
        if (_idleDemoActive)
        {
            _gesture?.StopImmediate();
            _performance?.Stop();
            _dance?.StopImmediate();
            _idleDemoActive = false;
            StopMotion(0.15);
        }

        _petIdleActions?.NotifyInteraction();
        _idleRandomGestureTimer = Math.Max(_idleRandomGestureTimer, 3.0);
    }

    /// <summary>互动是否要拒绝新交互：用户动作 / 表演 / 舞蹈进行中；互动自己的手势动作 / 待机随机演示不算。</summary>
    private bool IsInteractionBlocked()
    {
        if (_interaction?.IsInteracting == true) return false; // 互动自己的手指 / 护住手势动画不算「用户动作」
        if (HasUserMotionPlaying) return true;                 // 用户点播的动作进行中（显式播放 IDLE 这类待机候选不算）
        if (!_idleDemoActive && (_performance?.HasActive == true || _dance?.HasActive == true)) return true;
        return false;
    }

    /// <summary>待机小动作的让位条件：互动 / 说话 / 音乐 / 动捕 / 演示 / 用户动作都不抢。</summary>
    private bool IsPetIdleBlocked(bool mocapFace)
    {
        if (mocapFace || _speechActive) return true;
        if (MusicEnabled && _musicPlaying) return true;
        if (_gesture?.HasActive == true || _performance?.HasActive == true || _dance?.HasActive == true) return true;
        if (HasUserMotionPlaying) return true;
        if (_interaction?.IsInteracting == true || (_interaction?.CooldownRemaining ?? 0) > 0.01) return true;
        return false;
    }

    /// <summary>
    /// 有没有「用户点播的非待机动作」在播。显式点播 IDLE 这类待机候选不算忙——它本来就是待机，
    /// 待机小动作 / 随机手势照常可以叠在它上面（和旧版「待机动作不压制待机小动作」的口径一致）。
    /// </summary>
    private bool HasUserMotionPlaying
        => _motionPlayer.HasActive && _motionPlayer.Current?.Entry.IsIdleCandidate != true;

    /// <summary>待机小动作能不能顺带挂表情：用户已经有表情（含额头三态）时不动它，避免撤掉用户的表情。</summary>
    private bool CanPetIdleUseExpression()
        => _interaction?.HasFaceExpression != true && !_expressionManager.HasActive;

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

    /// <summary>表演里触发的手势（手势是比表演更高的层，直接叠在表演上面）。</summary>
    private void StartGestureForPerformance(string id) => _gesture?.Play(id);

    /// <summary>表演里触发的手势停下（表演结束 / 被停时）。</summary>
    private void StopGestureForPerformance() => _gesture?.Stop();

    /// <summary>解析语音口型参数。</summary>
    private void ResolveSpeechParameters(CubismModel model)
    {
        _speechMouthOpenY = model.FindParameter("ParamMouthOpenY");
        _speechMouthForm = model.FindParameter("ParamMouthForm");
        _speechJawOpen = model.FindParameter("JawOpen");
        ResolveMoodPuffParameters(model);
        ResolveFeedingParameters(model);
        _wiggleHeadAngleZ = model.FindParameter("ParamAngleZ") ?? model.FindParameter("AngleZ");
        _wiggleBodyAngleZ = model.FindParameter("ParamBodyAngleZ");
        _obsEyeL = model.FindParameter("ParamEyeLOpen");
        _obsEyeR = model.FindParameter("ParamEyeROpen");
        _obsSquintL = model.FindParameter("LEyeSquint");
        _obsSquintR = model.FindParameter("REyeSquint");
        _obsEyeBallX = model.FindParameter("ParamEyeBallX");
        _obsEyeBallY = model.FindParameter("ParamEyeBallY");
        _obsAngleZ = _wiggleHeadAngleZ;
        _obsBodyAngleZ = _wiggleBodyAngleZ;
        _obsBrowLY = model.FindParameter("ParamBrowLY");
        _obsBrowRY = model.FindParameter("ParamBrowRY");
        _obsBrowLAngle = model.FindParameter("ParamBrowLAngle");
        _obsBrowRAngle = model.FindParameter("ParamBrowRAngle");
        _obsMouthForm = model.FindParameter("ParamMouthForm");
        _tailRotationParams = new CubismParameter?[TailWag.SegmentCount];
        for (var i = 0; i < TailWag.SegmentCount; i++)
        {
            _tailRotationParams[i] = model.FindParameter("Param_Angle_Rotation" + (84 + i));
        }
        _tailSwitch = model.FindParameter("Clothes5");
        _pokeAngleX = model.FindParameter("ParamAngleX") ?? model.FindParameter("AngleX");
        _pokeMouthForm = model.FindParameter("ParamMouthForm");
        _pokeMouthSmileL = model.FindParameter("MouthSmile_L");
        _pokeMouthSmileR = model.FindParameter("MouthSmile_R");
    }

    /// <summary>解析投喂 / 吃饭要写的参数（模型没有的就跳过）。</summary>
    private void ResolveFeedingParameters(CubismModel model)
    {
        _feedAngleX = model.FindParameter("ParamAngleX") ?? model.FindParameter("AngleX");
        _feedAngleY = model.FindParameter("ParamAngleY") ?? model.FindParameter("AngleY");
        _feedBodyAngleX = model.FindParameter("ParamBodyAngleX");
        _feedEyeBallX = model.FindParameter("ParamEyeBallX");
        _feedEyeBallY = model.FindParameter("ParamEyeBallY");
        _feedMouthOpenY = model.FindParameter("ParamMouthOpenY");
        _feedMouthForm = model.FindParameter("ParamMouthForm");
        _feedJawOpen = model.FindParameter("JawOpen");
    }

    /// <summary>推进投喂 / 吃饭状态机（每帧都要走：靠近 / 嚼嚼嚼 / 吞咽的计时不依赖这一帧有没有写参数）。</summary>
    private void TickFeeding(double deltaSeconds) => _feeding.Update(deltaSeconds);

    /// <summary>
    /// 投喂 / 吃饭输出：饭团靠近时头 / 身体 / 眼睛朝它偏、张嘴；吃到后嚼嚼嚼 → 伪吞咽。
    /// <para>是比表情更高的层（嘴型要压过表情）。动捕有脸时整段让位（真人脸优先），说话时只让出嘴（口型跟着语音）；
    /// 输出结束（嚼完 / 饭团拿走）后本层拿最后一帧姿势 0.2 秒交回下面各层。</para>
    /// </summary>
    private void WriteFeeding(bool mocapFace)
    {
        if (!_feeding.HasOutput || mocapFace) return;

        var fx = _feeding.FocusX;
        var fy = _feeding.FocusY;
        var strength = _feeding.Strength;
        var mouth = Math.Clamp(_feeding.MouthOpen, 0f, 1f);

        WriteFeedParameter(_feedAngleX, (fx * strength * 22f) + 0f);
        WriteFeedParameter(_feedAngleY, (-fy * strength * 15f) + _feeding.HeadNodDegrees);
        WriteFeedParameter(_feedBodyAngleX, _feeding.BodyLeanDegrees);
        WriteFeedParameter(_feedEyeBallX, fx * 0.9f);
        WriteFeedParameter(_feedEyeBallY, -fy * 0.9f);

        // 说话时嘴型跟着语音，不抢
        if (_speechActive) return;
        WriteFeedParameter(_feedMouthOpenY, mouth);
        WriteFeedParameter(_feedMouthForm, mouth > 0.04f ? 0.15f : 0f);
        WriteFeedParameter(_feedJawOpen, mouth * 0.55f);
    }

    private static void WriteFeedParameter(CubismParameter? parameter, float value)
    {
        if (parameter is not null) parameter.Value = value;   // setter 自带 min~max 夹取
    }

    /// <summary>解析鼓嘴参数（模型没有这些参数的自定义模型会自动跳过）。</summary>
    private void ResolveMoodPuffParameters(CubismModel model)
    {
        _moodCheekPuff = model.FindParameter("CheeckPuff") ?? model.FindParameter("CheekPuff");
        _moodCheekPuffL = model.FindParameter("CheeckPuffL") ?? model.FindParameter("CheekPuffL");
        _moodCheekPuffR = model.FindParameter("CheeckPuffR") ?? model.FindParameter("CheekPuffR");
        _moodPucker = model.FindParameter("Mouthpucker") ?? model.FindParameter("MouthPucker");
    }

    /// <summary>
    /// 写鼓嘴：数值按 0~1 平滑逼近目标（约 0.2 秒），写左右脸颊鼓起 + 轻微撅嘴。
    /// 目标 / 当前都接近 0 时不写 = 鼓嘴层没有输出（用户手动调的脸颊参数照常生效）。
    /// </summary>
    private void ApplyMoodPuff(double deltaSeconds)
    {
        var target = _moodPuffTarget;
        var current = _moodPuffCurrent;
        if (Math.Abs(target - current) < 0.001f)
        {
            _moodPuffCurrent = current = target;
        }
        else
        {
            var speed = Math.Clamp(deltaSeconds * 6.0, 0.0, 1.0);
            current += (target - current) * (float)speed;
            _moodPuffCurrent = current;
        }

        if (current <= 0.002f) return;

        WriteMoodPuffParameter(_moodCheekPuff, current * 0.75f);
        WriteMoodPuffParameter(_moodCheekPuffL, current * 0.45f);
        WriteMoodPuffParameter(_moodCheekPuffR, current * 0.45f);
        WriteMoodPuffParameter(_moodPucker, current * 0.35f);
    }

    private static void WriteMoodPuffParameter(CubismParameter? parameter, float value)
    {
        if (parameter is null) return;
        parameter.Value = parameter.Maximum > parameter.DefaultValue
            ? parameter.DefaultValue + (parameter.Maximum - parameter.DefaultValue) * value
            : value;
    }

    /// <summary>
    /// 语音口型：优先级「动捕有脸 &gt; 语音 &gt; 音乐」。语音在说话且没有动捕脸时写嘴，
    /// 让音乐跟唱在语音时闭嘴（见 <see cref="ApplyMusicResponse"/>）。
    /// </summary>
    private void ApplySpeechMouth(bool mocapFace)
    {
        if (!_speechActive || mocapFace) return;
        var open = Math.Clamp(_speechMouthOpen, 0f, 1f);
        SetSpeechParameter(_speechMouthOpenY, open);
        SetSpeechParameter(_speechMouthForm, 0.15f);
        SetSpeechParameter(_speechJawOpen, open * 0.5f);
    }

    private static void SetSpeechParameter(CubismParameter? parameter, float value)
    {
        if (parameter is not null) parameter.Value = value;
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

    /// <summary>待机小动作的「忙碌」判定：有动捕 / 音乐 / 语音 / 演示 / 用户动作 / 互动都算忙。</summary>
    private bool IsIdleRandomBusy(bool mocapFace)
    {
        if (mocapFace || _speechActive) return true;
        if (MusicEnabled && _musicPlaying) return true;
        if (_gesture?.HasActive == true || _performance?.HasActive == true || _dance?.HasActive == true) return true;
        if (HasUserMotionPlaying) return true;
        if (_interaction?.IsInteracting == true || (_interaction?.CooldownRemaining ?? 0) > 0.01) return true;
        if (_petIdleActions?.HasActive == true) return true;
        return false;
    }

    private bool CanPlayIdleRandomDemo(bool mocapFace)
    {
        if (IsIdleRandomBusy(mocapFace)) return false;
        return true;
    }

    private bool TryPlayRandomIdleDemo(bool mocapFace, string? forcedGestureId = null)
    {
        if (!CanPlayIdleRandomDemo(mocapFace)) return false;
        var gestureIds = _gesture?.Ids;
        var performanceIds = _performance?.Ids;
        var gestureCount = gestureIds?.Count ?? 0;
        var performanceCount = performanceIds?.Count ?? 0;
        var total = gestureCount + performanceCount;
        if (total == 0) return false;

        bool played;
        if (!string.IsNullOrWhiteSpace(forcedGestureId))
        {
            if (_gesture?.IsKnownId(forcedGestureId) != true) return false;
            // 待机随机手势统一限时：姿势摆好后 2.5 秒自动播退出动作，狗尾草 / 竖食指不会一直举着。
            played = PlayGesture(forcedGestureId.Trim(), IdleRandomGestureHoldSeconds);
        }
        else
        {
            var pick = _idleRandomRng.Next(total);
            played = pick < gestureCount
                ? PlayGesture(gestureIds![pick], IdleRandomGestureHoldSeconds)
                : PlayPerformance(performanceIds![pick - gestureCount]);
        }

        if (!played) return false;
        _idleRandomGestureCount++;
        _idleDemoActive = true;
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

    /// <summary>写一个音乐响应参数（音乐层求值期间调用，落到本层捕获缓冲）。</summary>
    private static void SetMusicParameter(CubismParameter? parameter, float value)
    {
        if (parameter is not null) parameter.Value = value;
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
        var eyeBallYMax = Math.Max(1.0f, _mocapEyeBallY?.Maximum ?? 1.0f);
        SetParameter(_mocapEyeBallY, Math.Clamp((float)result.PupilY * eyeStrength, -1.0f, eyeBallYMax));

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

    /// <summary>本帧时间步长（各层求值期间要用的小函数共享）。</summary>
    private double _frameDeltaSeconds;

    /// <summary>
    /// 推进一帧（在 UI 线程的 DispatcherTimer 里调）：
    /// <list type="number">
    ///   <item>推进「不管哪一层有没有输出都要走」的状态（动捕读取 / 眨眼判定 / 限时反应计时 / 投喂状态机）；</item>
    ///   <item>设置各常驻层这一帧求不求值；</item>
    ///   <item>位姿合成：out = 手动值 → 逐层求值按权重混合 → 写回 Core；</item>
    ///   <item>物理 / 摇尾巴 / 固定参数（直通读写 Core）→ 待机随机小动作调度 → Core <c>Update()</c>。</item>
    /// </list>
    /// </summary>
    public void Tick(TimeSpan delta)
    {
        var model = _model;
        var pose = _pose;
        if (model is null || pose is null || _disposed) return;

        var startTimestamp = Stopwatch.GetTimestamp();
        var deltaSeconds = Math.Clamp(delta.TotalSeconds, 0.0, 0.25);
        _frameDeltaSeconds = deltaSeconds;
        _time += deltaSeconds;

        // ---- 0) 与层无关的每帧状态 ----
        var mocapFace = UpdateMocapState(out var mocapResult);
        var blinkActive = AutoBlink && !(mocapFace && MocapBlinkEnabled);
        TickReactionTimers(deltaSeconds);
        TickFeeding(deltaSeconds);

        var idleEnabled = IdleEnabled;
        var gestureActive = _gesture?.HasActive == true;
        var performanceActive = _performance?.HasActive == true;
        var danceActive = _dance?.HasActive == true;
        EyeTrackingPaused = ShouldPauseEyeTracking(
            EyeTrackingPauseOnAction, IsObserving, gestureActive, performanceActive, danceActive,
            _interaction?.IsInteracting == true, IsPokeReacting);

        // ---- 1) 常驻层这一帧求不求值 ----
        var swayActive = SwayEnabled || _swayDemoRemaining > 0;
        var idleMotionActive = idleEnabled && IdleMotion is not null;
        _idleAutoStage!.Active = idleEnabled || blinkActive;
        _eyeStage!.Active = !mocapFace && !EyeTrackingPaused && (idleEnabled || EyeTrackingEnabled);
        _mocapStage!.Active = mocapFace;
        _swayStage!.Active = swayActive;
        _speechStage!.Active = _speechActive && !mocapFace;
        _moodStage!.Active = _moodPuffTarget > 0f || _moodPuffCurrent > 0.002f;
        _idleMotionStage!.Active = idleMotionActive;
        _userMotionStage!.Active = _motionPlayer.HasActive;
        _expressionStage!.Active = _expressionManager.HasActive;
        _pokeStage!.Active = IsPokeReacting;
        _shyStage!.Active = IsShyWiggling;
        _observationStage!.Active = IsObserving;
        _feedingStage!.Active = _feeding.HasOutput;

        if (!idleMotionActive) _idleMotionPlayer.Clear();
        if (!swayActive) _bodySway?.ClearApplied();   // UI 显示「没有在晃」

        // ---- 2) 位姿合成（顺序 = 优先级，见 CreatePoseStages） ----
        pose.SetDeltaSeconds(deltaSeconds);
        pose.BeginFrame();

        pose.Run(_idleAutoStage, () => _idle?.Update(_time, deltaSeconds, blinkActive, idleEnabled, idleEnabled));
        pose.Run(_eyeStage, UpdateEyeTracking);
        pose.Run(_mocapStage, () => ApplyMocapFace(mocapResult!));
        pose.Run(_swayStage, () => UpdateBodySway(deltaSeconds, mocapFace));
        pose.Run(_musicStage, () => ApplyMusicResponse(mocapFace));
        UpdateBodyLean(deltaSeconds);
        pose.Run(_speechStage, () => ApplySpeechMouth(mocapFace));
        pose.Run(_moodStage, () => ApplyMoodPuff(deltaSeconds));
        pose.Run(_idleMotionStage, UpdateIdleMotion);
        pose.Run(_userMotionStage, UpdateUserMotion);
        pose.Run(_danceStage, () => _dance?.Update(deltaSeconds));
        pose.Run(_performanceStage, () => _performance?.Update(deltaSeconds, allowTalk: !mocapFace && !_speechActive));
        pose.Run(_gestureStage, () => _gesture?.Update(deltaSeconds, _eyeBlinkParameterIds, _lipSyncParameterIds));
        pose.Run(_petIdleStage, () => _petIdleActions?.Update(deltaSeconds, IsPetIdleBlocked(mocapFace)));
        pose.Run(_interactionStage, () => _interaction?.Update(deltaSeconds, IsInteractionBlocked(), mocapFace, _speechActive));
        pose.Run(_expressionStage, () =>
        {
            UpdateTemporaryExpressions();
            _expressionManager.Update(model, deltaSeconds);
        });
        pose.Run(_pokeStage, () => WritePokeReaction());
        pose.Run(_shyStage, () => WriteShyWiggle());
        pose.Run(_observationStage, () => WriteObservation());
        pose.Run(_feedingStage, () => WriteFeeding(mocapFace));

        // 待机随机挑的演示播完了，标记跟着放下
        if (_idleDemoActive && !gestureActive && !performanceActive && !danceActive) _idleDemoActive = false;

        pose.EndFrame();

        // ---- 3) 物理：读合成好的参数当输入，输出直接写 Core（直通，不进手动值） ----
        if (PhysicsEnabled && _physics is not null)
        {
            pose.RunBypass(() => _physics.Evaluate(deltaSeconds, (float)Math.Clamp(PhysicsStrength, 0.0, 1.0)));
        }

        // 摇尾巴：物理把尾巴角度算完后，再叠加一段带相位延迟的摆动（物理关着也能摇）
        pose.RunBypass(() => ApplyTailWag(deltaSeconds));

        // 固定参数（面板「锁定」）最终回填：物理输出也会写 ParamAngleX 这类输入兼输出的参数，
        // 物理算完之后再钉一次，锁定的姿势才站得住
        if (_parameterOverrides.Count > 0) pose.RunBypass(() => ApplyParameterOverrides());

        // 待机随机小动作：没有动捕 / 音乐 / 语音 / 舞蹈 / 表演 / 互动 / 用户动作时，按随机间隔播一个
        UpdateIdleRandomGesture(deltaSeconds, mocapFace);

        model.Update();
        // 顶点已更新：互动热区几何缓存失效，下一次 HitTest 用本帧蒙皮重新算
        _interaction?.InvalidateGeometry();
        _lastTickMilliseconds = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        FrameUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>鼠标眼控层：把运行时属性同步给控制器并求值（只在层活动时调用）。</summary>
    private void UpdateEyeTracking()
    {
        var eye = _eye;
        if (eye is null) return;
        eye.Smoothing = EyeTrackingSmoothing;
        eye.HeadStrength = EyeTrackingHeadStrength;
        eye.EyeStrength = EyeTrackingEyeStrength;
        eye.BodyStrength = EyeTrackingBodyStrength;
        eye.HorizontalGain = EyeTrackingHorizontalGain;
        eye.UpGain = EyeTrackingUpGain;
        eye.DownGain = EyeTrackingDownGain;
        eye.VerticalOffset = EyeTrackingVerticalOffset;

        var idleEnabled = IdleEnabled;
        eye.Update(_frameDeltaSeconds, EyeTrackingEnabled,
            idleEnabled ? _idle?.SwayAngleZ ?? 0f : 0f,
            idleEnabled ? _idle?.SwayBodyAngleZ ?? 0f : 0f);
    }

    /// <summary>
    /// 待机动作层：一直在最底下循环播 <see cref="IdleMotion"/>（换了待机动作就在同一个混合器里交叉淡化过去）。
    /// 手势 / 用户动作是它上面的层，收尾时自然露出这里的姿势，不需要「压制待机 / 提前接回待机」之类的特判。
    /// </summary>
    private void UpdateIdleMotion()
    {
        var model = _model;
        if (model is null) return;

        if (IdleMotion is { } idleMotion
            && _motionDataByPath.TryGetValue(idleMotion.FilePath, out var idleData)
            && !string.Equals(_idleMotionPlayer.Current?.Entry.FilePath, idleMotion.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            _idleMotionPlayer.Play(model, idleMotion, idleData, loop: true, -1);
        }

        _idleMotionPlayer.Update(model, _frameDeltaSeconds, _eyeBlinkParameterIds, _lipSyncParameterIds);
    }

    /// <summary>用户动作层：工具页 / 桌宠点播的 motion3（混合器自带新旧动作交叉淡化）。</summary>
    private void UpdateUserMotion()
    {
        var model = _model;
        if (model is null) return;
        _motionPlayer.Update(model, _frameDeltaSeconds, _eyeBlinkParameterIds, _lipSyncParameterIds);
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
        _idleDemoActive = false;
    }

    /// <summary>停止当前动作（按 <paramref name="fadeSeconds"/> 淡出；≤0 = 立即）。</summary>
    public void StopMotion(double fadeSeconds = 0.25) => _motionPlayer.Stop(fadeSeconds);

    /// <summary>
    /// 施加表情（按 fadeSeconds 淡入；≤0 = 立即）。
    /// 用户手动施加同一个表情时，会取消它之前由互动临时挂上的到期计时（用户选择优先，不被自动撤掉）。
    /// </summary>
    public void ApplyExpression(ExpressionEntry expression, double fadeSeconds = 0.3)
    {
        ArgumentNullException.ThrowIfNull(expression);
        CancelTemporaryExpression(expression);
        ApplyExpressionCore(expression, fadeSeconds);
    }

    /// <summary>
    /// 施加一个「互动临时表情」：fadeInSeconds 淡入，保持 holdSeconds 后自动 RemoveExpression（0.5s 淡出）。
    /// 同一表情在临时期间再次触发只延长到期时间，不重复淡入；用户已经手动开着的同一表情不会被接管。
    /// </summary>
    public void ApplyExpressionTemporarily(ExpressionEntry expression, double holdSeconds,
        double fadeInSeconds = 0.25)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!_expressionDataByPath.ContainsKey(expression.FilePath)) return;

        var seconds = double.IsFinite(holdSeconds) ? Math.Clamp(holdSeconds, 0.5, 60.0) : 3.0;
        if (_temporaryExpressions.TryGetValue(expression.FilePath, out var existing))
        {
            // 已经在临时表情里：只续期，避免抚摸时每帧重新淡入造成闪烁。
            _temporaryExpressions[expression.FilePath] = (existing.Entry, _time + seconds);
            return;
        }

        // 用户自己开着的表情（稳定生效，不是淡出尾巴）：不挂到期计时，避免互动结束后误撤用户的表情。
        if (_expressionManager.IsActive(expression) && !_expressionManager.IsFadingOut(expression)) return;

        ApplyExpressionCore(expression, fadeInSeconds);
        _temporaryExpressions[expression.FilePath] = (expression, _time + seconds);
    }

    /// <summary>清除所有表情（按 fadeSeconds 淡出；同时清掉临时表情的到期计时）。</summary>
    public void ClearExpressions(double fadeSeconds = 0.3)
    {
        _temporaryExpressions.Clear();
        _expressionManager.Clear(fadeSeconds);
    }

    /// <summary>
    /// 只移除一个表情（多选叠加时取消其中一项；其它表情继续）。
    /// 表情卡改成多选开关后，UI 用这个接口逐项取消。
    /// </summary>
    public void RemoveExpression(ExpressionEntry expression, double fadeSeconds = 0.3)
    {
        ArgumentNullException.ThrowIfNull(expression);
        CancelTemporaryExpression(expression);
        _expressionManager.Remove(expression, fadeSeconds);
    }

    private void ApplyExpressionCore(ExpressionEntry expression, double fadeSeconds)
    {
        if (!_expressionDataByPath.TryGetValue(expression.FilePath, out var data))
        {
            _options.Log?.Invoke($"表情没有解析数据，无法施加：{expression.Name}");
            return;
        }

        _expressionManager.Apply(expression, data, fadeSeconds);
    }

    private void CancelTemporaryExpression(ExpressionEntry expression)
    {
        if (expression is null) return;
        _temporaryExpressions.Remove(expression.FilePath);
    }

    /// <summary>每帧清理到期的互动临时表情（在 ExpressionManager.Update 之前调用）。</summary>
    private void UpdateTemporaryExpressions()
    {
        if (_temporaryExpressions.Count == 0) return;
        List<string>? expired = null;
        foreach (var pair in _temporaryExpressions)
        {
            if (_time < pair.Value.Until) continue;
            (expired ??= new List<string>(2)).Add(pair.Key);
        }

        if (expired is null) return;
        foreach (var path in expired)
        {
            if (!_temporaryExpressions.Remove(path, out var item)) continue;
            _expressionManager.Remove(item.Entry, 0.5);
        }
    }

    /// <summary>当前生效（含正在淡出）的表情个数。</summary>
    public int ActiveExpressionCount => _expressionManager.ActiveCount;

    /// <summary>某个表情当前是否生效（UI 勾选状态用）。</summary>
    public bool IsExpressionActive(ExpressionEntry expression) => _expressionManager.IsActive(expression);

    /// <summary>
    /// 把所有参数 / 部件恢复默认（表情 / 动作 / 晃动测试 / 各类限时反应也停掉）。
    /// 手动值清成模型默认值；位姿合成器下一帧「直接到位」——常驻层（呼吸 / 眼控）立刻按当前开关生效，
    /// 动作层立即清空，不做淡入淡出。
    /// </summary>
    public void ResetParameters()
    {
        var model = _model;
        if (model is null) return;

        model.ResetParametersToDefault();   // 经过合成器：手动值 + Core 一起回默认
        model.ResetPartOpacities();
        _motionPlayer.Clear();
        _idleMotionPlayer.Clear();
        _temporaryExpressions.Clear();
        _expressionManager.ClearImmediate();
        _physics?.Reset();
        _idle?.Reset(_time);
        _eye?.ClearTarget();
        _bodySway?.Reset();
        _swayDemoRemaining = 0;
        _gesture?.StopImmediate();
        _performance?.Stop();
        _dance?.StopImmediate();
        _interaction?.StopImmediate();
        _petIdleActions?.StopImmediate();
        _idleDemoActive = false;
        _moodPuffTarget = 0f;
        _moodPuffCurrent = 0f;
        _feeding.Reset();
        _idleRandomGestureTimer = 0;
        _pokeReactionRemainingSeconds = 0;
        _wiggleRemainingSeconds = 0;
        _obsRemainingSeconds = 0;
        _obsFacePose = null;
        _tailWagRemainingSeconds = 0;
        _pose?.SnapAll();
        model.Update();
    }

    /// <summary>重新找待机动作（改完 IdleMotion 或想立刻恢复待机时调）。</summary>
    public void ReloadIdle()
    {
        if (IdleMotion is null || !_motions.Contains(IdleMotion))
            IdleMotion = _motions.FirstOrDefault(m => m.IsIdleCandidate) ?? _motions.FirstOrDefault();

        // 待机动作是常驻层：换了 IdleMotion，下一帧 UpdateIdleMotion 会在同一个混合器里交叉淡化到新的那个
    }

    /// <summary>每帧 Tick 完触发（UI 刷新参数值 / 渲染照它来）。</summary>
    public event EventHandler? FrameUpdated;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        FrameUpdated = null;
        _parameterOverrides.Clear();
        _parameterEffect = null;
        _model?.AttachParameterSink(null);
        _pose = null;
        _idleAutoStage = null;
        _eyeStage = null;
        _mocapStage = null;
        _swayStage = null;
        _musicStage = null;
        _speechStage = null;
        _moodStage = null;
        _idleMotionStage = null;
        _userMotionStage = null;
        _danceStage = null;
        _performanceStage = null;
        _gestureStage = null;
        _petIdleStage = null;
        _interactionStage = null;
        _expressionStage = null;
        _pokeStage = null;
        _shyStage = null;
        _observationStage = null;
        _feedingStage = null;
        _deadParameterIndices = new HashSet<int>();
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
        _temporaryExpressions.Clear();
        _motionPlayer.Clear();
        _idleMotionPlayer.Clear();
        _expressionManager.ClearImmediate();
        _gesture = null;
        _performance = null;
        _dance = null;
        _speechMouthOpenY = null;
        _speechMouthForm = null;
        _speechJawOpen = null;
        _speechActive = false;
        _speechMouthOpen = 0;
        _idleRandomGestureTimer = 0;
        _interaction = null;
        _petIdleActions = null;
        _idleDemoActive = false;
        _lastInteractionVoiceEvent = null;
        _lastInteractionName = null;
        InteractionHappened = null;
        _motions = Array.Empty<MotionEntry>();
        _expressions = Array.Empty<ExpressionEntry>();
        _parameterBindings = Array.Empty<ParameterBinding>();
        _partBindings = Array.Empty<PartBinding>();
        _eyeBlinkParameterIds = Array.Empty<string>();
        _lipSyncParameterIds = Array.Empty<string>();
    }

    private void ReportProgress(double progress) => _options.Progress?.Invoke(Math.Clamp(progress, 0.0, 1.0));
}
