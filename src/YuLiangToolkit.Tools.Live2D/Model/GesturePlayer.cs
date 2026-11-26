using YuLiangToolkit.Tools.Live2D.Interop;
using YuLiangToolkit.Tools.Live2D.Model.Control;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>参数姿势目标（参数 id + 目标值）。</summary>
internal readonly struct PoseTarget
{
    public PoseTarget(string id, float value)
    {
        Id = id;
        Value = value;
    }

    public string Id { get; }
    public float Value { get; }
}

/// <summary>演示系统共用的小工具（缓动 / 限幅），避免各控制器各写一份。</summary>
internal static class DemoMath
{
    /// <summary>平滑阶跃 3t²-2t³（0→0、1→1，两端速度为 0）。</summary>
    public static float SmoothStep(float t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return t * t * (3f - (2f * t));
    }

    /// <summary>在 default 与 target 之间按权重插值（参数默认值作为「回正」基准）。</summary>
    public static float BlendFromDefault(CubismParameter parameter, float target, float weight)
    {
        var from = parameter.DefaultValue;
        return from + ((target - from) * weight);
    }
}

/// <summary>
/// 手势播放器：一段手势 = 一个「片段」（<see cref="Clip"/>），本层里最多同时有两段——
/// 正在播的 <c>_current</c> 和被换掉的 <c>_outgoing</c>（0.25 秒淡出）。
///
/// <para><b>原生手势</b>（作者留了进入 / 退出动作）的片段是个小状态机：</para>
/// <list type="number">
///   <item><b>Enter</b>：播作者 enter 动作。动作在临时捕获缓冲里求值，读到的「当前值」是下面各层合成好的姿势，
///     所以淡入 = 从当前姿势过渡到动作姿势（<see cref="MotionPlayer"/> 自己的淡入权重）。</item>
///   <item><b>Hold</b>：enter 播完，把 enter 曲线末值当保持姿势逐帧写（和动作最后一帧完全相等，无缝）。</item>
///   <item><b>Exit</b>：先把「这段手势最后一帧的输出」（= 保持姿势 / 抬到一半的姿势）写进缓冲当起点，
///     再播作者 exit 动作——exit 是从这个起点淡入的，不是从默认值。</item>
///   <item>exit 播完 → 片段结束，本层不再有输出；合成器拿它最后一帧（exit 末姿势）按层的淡出时间交回待机。</item>
/// </list>
///
/// <para><b>参数姿势</b>（作者没留动作）：片段输出 = 目标值，权重 0.35 秒淡入、0.25 秒淡出。</para>
///
/// <para><b>收回不再抽搐</b>：旧实现把参数直接写进模型，靠「帧首复位 + 往保持姿势拉的交接 + 提前接回待机」
/// 三个补丁凑连续性，任何一处时序错了手就先掉回默认再抬起来。现在每一拍的起点都是「上一拍真实的输出」，
/// 由合成器按权重混合，没有「回到默认」这个中间状态。</para>
/// </summary>
internal sealed class GesturePlayer
{
    public const string IdHandOnChest = "抱胸致意";
    public const string IdPoint = "竖起食指";
    public const string IdGrass = "拿起狗尾草";
    public const string IdWave = "举手打招呼";
    public const string IdHeart = "比心";
    public const string IdAkimbo = "叉腰";
    public const string IdHeadHold = "抱头";
    public const string IdHandsDown = "放下手";

    private static readonly string[] GestureIdList =
    {
        IdHandOnChest, IdPoint, IdGrass, IdWave, IdHeart, IdAkimbo, IdHeadHold, IdHandsDown,
    };

    /// <summary>
    /// 参数姿势：作者没有对应原生动作，直接给语义手臂参数目标值（-1~1，默认 0）。
    /// <para>同款模型的「语义臂」实际可见幅度有限（手臂大多藏在裙后，只有小臂/手露出来），
    /// 这些值是按沙盒渲染逐组核对后取的「正方向可见」值：LArm/RArm 为正 = 手臂向外抬起，
    /// LForearm/RForearm 为正 = 小臂向内收、为负 = 向外翻；再叠加少量头 / 眼 / 眉参数让每个姿势更好认。</para>
    /// </summary>
    private static readonly PoseTarget[][] ParamPoseTargets =
    {
        // 举手打招呼：左臂抬起挥手 + 轻歪头 + 开心眼
        new[]
        {
            new PoseTarget("LArm", 1.00f), new PoseTarget("LForearm", 0.80f),
            new PoseTarget("LWrist", 0.60f), new PoseTarget("LHand", 0.40f),
            new PoseTarget("ShoulderY", 12f), new PoseTarget("ParamAngleZ", 4f),
            new PoseTarget("ParamEyeBallY", 0.25f), new PoseTarget("EyeSmile", 0.40f),
        },
        // 比心：双手在身前靠拢 + 歪头卖萌
        new[]
        {
            new PoseTarget("LArm", 0.45f), new PoseTarget("LForearm", 1.00f),
            new PoseTarget("LWrist", -0.80f), new PoseTarget("LHand", 0.50f),
            new PoseTarget("RArm", 0.45f), new PoseTarget("RForearm", 1.00f),
            new PoseTarget("RWrist", 0.80f), new PoseTarget("RHand", 0.50f),
            new PoseTarget("ParamAngleZ", -4f), new PoseTarget("EyeSmile", 0.50f),
        },
        // 叉腰：双手压到胯侧、下巴微抬
        new[]
        {
            new PoseTarget("LArm", 0.55f), new PoseTarget("LForearm", -0.95f),
            new PoseTarget("LWrist", -0.20f),
            new PoseTarget("RArm", 0.55f), new PoseTarget("RForearm", -0.95f),
            new PoseTarget("RWrist", 0.20f), new PoseTarget("ParamAngleY", -2f),
        },
        // 抱头：双臂向外上方抬起 + 微微仰头（模型手臂贴头幅度有限，靠头颈一起表现；
        // BrowInnerUp 实测无效已移除——参数有效性探测确认它不驱动任何画面）
        new[]
        {
            new PoseTarget("LArm", 1.00f), new PoseTarget("LForearm", -0.60f),
            new PoseTarget("LWrist", -0.80f), new PoseTarget("LHand", -0.20f),
            new PoseTarget("RArm", 1.00f), new PoseTarget("RForearm", -0.60f),
            new PoseTarget("RWrist", 0.80f), new PoseTarget("RHand", -0.20f),
            new PoseTarget("ParamAngleY", 5f), new PoseTarget("ParamAngleZ", 2f),
        },
        // 放下手：全部回到自然垂手 / 正头（ShoulderX 实测无效已移除）
        new[]
        {
            new PoseTarget("LArm", 0.10f), new PoseTarget("LForearm", 0.05f),
            new PoseTarget("LWrist", 0.0f), new PoseTarget("LHand", 0.0f), new PoseTarget("LHand2", 0.0f),
            new PoseTarget("RArm", 0.10f), new PoseTarget("RForearm", 0.05f),
            new PoseTarget("RWrist", 0.0f), new PoseTarget("RHand", 0.0f), new PoseTarget("RHand2", 0.0f),
            new PoseTarget("ShoulderY", 0.0f),
            new PoseTarget("ParamAngleY", 0f), new PoseTarget("ParamAngleZ", 0f),
        },
    };

    /// <summary>作者原生手势：进入 / 退出动作名 + enter 动作缺失时的参数兜底（同款模型关节映射）。</summary>
    private sealed class NativeGesture
    {
        public required string Id { get; init; }
        public required string EnterMotionName { get; init; }
        public required string ExitMotionName { get; init; }
        public required PoseTarget[] FallbackTargets { get; init; }
    }

    private static readonly NativeGesture[] NativeGestures =
    {
        new()
        {
            Id = IdHandOnChest,
            EnterMotionName = "默认手切举手",
            ExitMotionName = "举手切默认手",
            FallbackTargets = new[]
            {
                new PoseTarget("Param62", 0.034f), new PoseTarget("Param59", 0.476f),
                new PoseTarget("Param60", 0.860f), new PoseTarget("Param61", 0.002f),
                new PoseTarget("Movement2", 1f),
            },
        },
        new()
        {
            Id = IdPoint,
            EnterMotionName = "竖手指切出来动画",
            ExitMotionName = "竖手指切回默认",
            FallbackTargets = new[]
            {
                new PoseTarget("Param39", 0.322f), new PoseTarget("Param41", 0.702f),
                new PoseTarget("Param42", 0f), new PoseTarget("Param43", 0f),
                new PoseTarget("Movement1", 0f), new PoseTarget("Movement3", 1f),
            },
        },
        new()
        {
            Id = IdGrass,
            EnterMotionName = "狗尾草切换出来",
            ExitMotionName = "狗尾草切回默认手",
            FallbackTargets = new[]
            {
                new PoseTarget("Param39", 0.200f), new PoseTarget("Param41", -0.732f),
                new PoseTarget("Param42", 0f), new PoseTarget("Param43", 0f),
                new PoseTarget("Movement1", 1f), new PoseTarget("Movement3", 0f),
            },
        },
    };

    private enum ClipPhase
    {
        Enter,
        Hold,
        Exit,
        Pose,
        Done,
    }

    /// <summary>一段手势（原生 = enter → hold → exit 状态机；参数姿势 = Pose + 权重淡入淡出）。</summary>
    private sealed class Clip
    {
        public string Id = "";
        public NativeGesture? Native;
        public int PoseIndex = -1;
        public ClipPhase Phase;

        /// <summary>保持多久自动还原（≤ 0 = 一直保持到手动停止）。</summary>
        public double AutoRestoreSeconds;

        /// <summary>进入 Hold 后累计的保持时间（不含 enter）。</summary>
        public double HoldElapsed;

        /// <summary>参数姿势的线性权重（0→1 淡入 / 1→0 淡出）。</summary>
        public float PoseWeight;

        /// <summary>参数姿势是否正在淡出。</summary>
        public bool Stopping;

        /// <summary>保持姿势（enter 曲线末值）。</summary>
        public CubismParameter?[] HoldParameters = Array.Empty<CubismParameter?>();
        public float[] HoldValues = Array.Empty<float>();
        public int HoldUsed;

        /// <summary>退出动作的起点 = 这段手势最后一帧的输出（从 Hold 退出时就是保持姿势）。</summary>
        public CubismParameter?[] ExitBaseParameters = Array.Empty<CubismParameter?>();
        public float[] ExitBaseValues = Array.Empty<float>();
        public int ExitBaseUsed;

        /// <summary>参数姿势的目标（建片段时解析；参数不存在的位置为 null）。</summary>
        public CubismParameter?[] PoseParameters = Array.Empty<CubismParameter?>();
        public float[] PoseValues = Array.Empty<float>();

        public readonly MotionPlayer Motions = new();
    }

    private const double EnterFadeSeconds = 0.12;
    private const double ExitFadeSeconds = 0.12;
    private const double PoseFadeInSeconds = 0.35;
    private const double PoseFadeOutSeconds = 0.25;

    /// <summary>被换掉的旧手势淡出时间（新手势的 enter / 淡入压在它上面交叉淡化）。</summary>
    private const double OutgoingFadeSeconds = 0.25;

    private readonly CubismModel _model;
    private readonly PoseCompositor _pose;
    private readonly Func<string, MotionEntry?> _findMotionEntry;
    private readonly Func<MotionEntry, Motion3Data?> _findMotionData;

    /// <summary>参数姿势解析好的目标（加载时一次；参数不存在的目标直接丢掉）。</summary>
    private readonly CubismParameter?[][] _paramPoseParameters;

    private readonly float[][] _paramPoseValues;

    private readonly Dictionary<string, float> _finalValueScratch = new(24);

    /// <summary>两段手势各自的捕获缓冲（复用，稳态 0 分配）。</summary>
    private readonly PoseCapture _currentCapture;
    private readonly PoseCapture _outgoingCapture;

    private Clip? _current;
    private Clip? _outgoing;
    private float _outgoingWeight;

    public GesturePlayer(
        CubismModel model,
        PoseCompositor pose,
        Func<string, MotionEntry?> findMotionEntry,
        Func<MotionEntry, Motion3Data?> findMotionData)
    {
        _model = model;
        _pose = pose;
        _findMotionEntry = findMotionEntry;
        _findMotionData = findMotionData;
        _currentCapture = new PoseCapture(model.ParameterCount);
        _outgoingCapture = new PoseCapture(model.ParameterCount);

        _paramPoseParameters = new CubismParameter?[ParamPoseTargets.Length][];
        _paramPoseValues = new float[ParamPoseTargets.Length][];
        for (var i = 0; i < ParamPoseTargets.Length; i++)
        {
            var targets = ParamPoseTargets[i];
            var parameters = new CubismParameter?[targets.Length];
            var values = new float[targets.Length];
            for (var j = 0; j < targets.Length; j++)
            {
                parameters[j] = model.FindParameter(targets[j].Id);
                values[j] = targets[j].Value;
            }

            _paramPoseParameters[i] = parameters;
            _paramPoseValues[i] = values;
        }
    }

    /// <summary>手势 id 是否存在（中文名 / jingyu 英文名都行）。</summary>
    public bool IsKnownId(string? id) => NormalizeId(id) is not null;

    /// <summary>该手势是否正在播（enter / hold / 参数姿势淡入保持中；退出动作播放期间不算）。</summary>
    public bool IsActive(string? id)
    {
        var normalized = NormalizeId(id);
        if (normalized is null || _current is null) return false;
        if (!string.Equals(_current.Id, normalized, StringComparison.Ordinal)) return false;
        return _current.Phase is ClipPhase.Enter or ClipPhase.Hold || (_current.Phase == ClipPhase.Pose && !_current.Stopping);
    }

    public IReadOnlyList<string> Ids => GestureIdList;

    /// <summary>
    /// 当前手势 id（没有为 null）。退出动作播放期间也算 null：那时的语义是「手势已经停了、正在收回」，
    /// 再点同一个手势会重新开始，互动的「竖食指 ⇄ 放下手」切换也靠它判断。
    /// </summary>
    public string? ActiveId => _current is { Phase: ClipPhase.Enter or ClipPhase.Hold or ClipPhase.Pose } clip
        ? clip.Id
        : null;

    /// <summary>是否还在播手势（含退出动作 / 被换掉的旧手势淡出）。</summary>
    public bool HasActive => _current is not null || _outgoing is not null;

    /// <summary>姿势是否已经摆好（enter 播完 / 参数淡入完成），自动还原从这一刻开始计时。</summary>
    public bool IsHolding => _current is { Phase: ClipPhase.Hold }
                             || (_current is { Phase: ClipPhase.Pose, Stopping: false } && _current.PoseWeight >= 1);

    /// <summary>
    /// 播一个手势。
    /// <paramref name="autoRestoreAfterHoldSeconds"/>：摆好姿势后保持多少秒自动播退出动作还原（&lt;= 0 = 手动停止）。
    /// 计时从 enter 动作播完 / 参数姿势淡入完成之后开始，所以不会把「抬手的过程」算进保持时间。
    /// 同一手势重复触发只刷新保持计时（不重播 enter）；换手势 = 旧的 0.25 秒淡出、新的照常进入。
    /// </summary>
    public bool Play(string id, double autoRestoreAfterHoldSeconds = 0)
    {
        var normalized = NormalizeId(id);
        if (normalized is null) return false;

        var autoRestore = double.IsFinite(autoRestoreAfterHoldSeconds)
            ? Math.Clamp(autoRestoreAfterHoldSeconds, 0, 60)
            : 0;
        if (IsActive(normalized))
        {
            if (autoRestore > 0)
            {
                _current!.AutoRestoreSeconds = autoRestore;
                _current.HoldElapsed = 0;
            }

            return true;
        }

        var clip = CreateClip(normalized, autoRestore);
        if (clip is null) return false;

        // 正在退出的那段也当旧手势一起淡出；已经有一段旧手势在淡出就直接丢掉（它的权重本来就 < 1）
        _outgoing = _current;
        _outgoingWeight = 1;
        _current = clip;
        return true;
    }

    /// <summary>停止手势：原生手势播退出动作（从当前保持姿势起笔）；参数姿势 0.25 秒淡出回待机。</summary>
    public void Stop()
    {
        var clip = _current;
        if (clip is null) return;
        switch (clip.Phase)
        {
            case ClipPhase.Enter or ClipPhase.Hold:
                BeginExit(clip);
                return;
            case ClipPhase.Pose when !clip.Stopping:
                clip.Stopping = true;
                return;
        }
    }

    /// <summary>立即释放（不播退出动作）：本层下一帧起没有输出，合成器拿最后一帧姿势按层的淡出时间交回。</summary>
    public void StopImmediate()
    {
        _current = null;
        _outgoing = null;
        _outgoingWeight = 0;
    }

    /// <summary>推进一帧（在位姿合成期、本层的捕获里调用）：旧手势淡出 → 当前手势求值 → 按权重混进本层。</summary>
    public void Update(double deltaSeconds, IReadOnlyList<string>? eyeBlinkParameterIds,
        IReadOnlyList<string>? lipSyncParameterIds)
    {
        if (_outgoing is not null)
        {
            _outgoingWeight -= (float)(deltaSeconds / OutgoingFadeSeconds);
            if (_outgoingWeight <= 0)
            {
                _outgoing = null;
                _outgoingWeight = 0;
            }
            else
            {
                EvaluateClip(_outgoing, deltaSeconds, _outgoingCapture, eyeBlinkParameterIds, lipSyncParameterIds);
                _pose.BlendIntoCurrent(_outgoingCapture, DemoMath.SmoothStep(_outgoingWeight));
            }
        }

        var clip = _current;
        if (clip is null) return;

        EvaluateClip(clip, deltaSeconds, _currentCapture, eyeBlinkParameterIds, lipSyncParameterIds);

        // 原生手势：进入 / 退出动作自带淡入，整段权重恒为 1；参数姿势：按自己的淡入淡出权重混
        var weight = clip.Native is not null ? 1f : DemoMath.SmoothStep(clip.PoseWeight);
        _pose.BlendIntoCurrent(_currentCapture, weight);

        if (clip.Phase == ClipPhase.Done) _current = null;
    }

    // ---------------- 片段求值 ----------------

    private void EvaluateClip(Clip clip, double deltaSeconds, PoseCapture capture,
        IReadOnlyList<string>? eyeBlinkParameterIds, IReadOnlyList<string>? lipSyncParameterIds)
    {
        _pose.CaptureTemporary(capture, () =>
        {
            switch (clip.Phase)
            {
                case ClipPhase.Enter:
                    clip.Motions.Update(_model, deltaSeconds, eyeBlinkParameterIds, lipSyncParameterIds);
                    if (clip.Motions.HasActive) return;
                    // enter 的最后一帧 = 曲线末值；下一帧起由保持姿势原样接住
                    clip.Phase = ClipPhase.Hold;
                    clip.HoldElapsed = 0;
                    WriteHold(clip);
                    return;

                case ClipPhase.Hold:
                    WriteHold(clip);
                    clip.HoldElapsed += deltaSeconds;
                    if (clip.AutoRestoreSeconds > 0 && clip.HoldElapsed >= clip.AutoRestoreSeconds)
                        BeginExit(clip);
                    return;

                case ClipPhase.Exit:
                    WriteExitBase(clip);
                    clip.Motions.Update(_model, deltaSeconds, eyeBlinkParameterIds, lipSyncParameterIds);
                    if (!clip.Motions.HasActive) clip.Phase = ClipPhase.Done;
                    return;

                case ClipPhase.Pose:
                    if (clip.Stopping)
                    {
                        clip.PoseWeight -= (float)(deltaSeconds / PoseFadeOutSeconds);
                        if (clip.PoseWeight <= 0)
                        {
                            clip.PoseWeight = 0;
                            clip.Phase = ClipPhase.Done;
                            return;
                        }
                    }
                    else
                    {
                        clip.PoseWeight += (float)(deltaSeconds / PoseFadeInSeconds);
                        if (clip.PoseWeight > 1) clip.PoseWeight = 1;
                    }

                    WritePoseTargets(clip);
                    return;
            }
        });
    }

    private void WriteHold(Clip clip)
    {
        for (var i = 0; i < clip.HoldUsed; i++)
        {
            var parameter = clip.HoldParameters[i];
            if (parameter is not null) parameter.Value = clip.HoldValues[i];
        }
    }

    private void WriteExitBase(Clip clip)
    {
        for (var i = 0; i < clip.ExitBaseUsed; i++)
        {
            var parameter = clip.ExitBaseParameters[i];
            if (parameter is not null) parameter.Value = clip.ExitBaseValues[i];
        }
    }

    private void WritePoseTargets(Clip clip)
    {
        var parameters = clip.PoseParameters;
        var values = clip.PoseValues;
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter is not null) parameter.Value = values[i];
        }
    }

    /// <summary>
    /// 开始退出：把这段手势「最后一帧的输出」存成退出动作的起点，再播作者的 exit 动作。
    /// 起点取自本段上一帧的捕获缓冲（Hold = 保持姿势；Enter 中途 = 抬到一半的姿势），
    /// exit 动作从这个起点淡入——这就是收回不抽搐的关键。
    /// </summary>
    private void BeginExit(Clip clip)
    {
        if (clip.Native is null)
        {
            clip.Stopping = true;
            return;
        }

        var source = ReferenceEquals(clip, _current) ? _currentCapture : _outgoingCapture;
        var count = source.WrittenCount;
        if (clip.ExitBaseParameters.Length < count)
        {
            clip.ExitBaseParameters = new CubismParameter?[count];
            clip.ExitBaseValues = new float[count];
        }

        var parameters = _model.Parameters;
        for (var i = 0; i < count; i++)
        {
            var index = source.Written[i];
            clip.ExitBaseParameters[i] = parameters[index];
            clip.ExitBaseValues[i] = source.Scratch[index];
        }

        clip.ExitBaseUsed = count;

        var entry = _findMotionEntry(clip.Native.ExitMotionName);
        var data = entry is null ? null : _findMotionData(entry);
        if (entry is null || data is null)
        {
            // 没有退出动作：本层不再输出，合成器拿保持姿势按层的淡出时间交回待机
            clip.Phase = ClipPhase.Done;
            return;
        }

        clip.Motions.Clear();
        clip.Motions.Play(_model, entry, data, loop: false, ExitFadeSeconds, 0);
        clip.Phase = ClipPhase.Exit;
    }

    // ---------------- 建片段 ----------------

    private Clip? CreateClip(string id, double autoRestore)
    {
        var native = FindNative(id);
        if (native is not null)
        {
            var entry = _findMotionEntry(native.EnterMotionName);
            var data = entry is null ? null : _findMotionData(entry);
            if (entry is not null && data is not null)
            {
                var clip = new Clip { Id = id, Native = native, AutoRestoreSeconds = autoRestore, Phase = ClipPhase.Enter };
                BuildHoldFromMotion(clip, data);
                clip.Motions.Play(_model, entry, data, loop: false, EnterFadeSeconds, 0);
                return clip;
            }

            // enter 动作缺失 → 参数姿势兜底（直接淡入保持）
            var fallbackParameters = new CubismParameter?[native.FallbackTargets.Length];
            var fallbackValues = new float[native.FallbackTargets.Length];
            for (var i = 0; i < native.FallbackTargets.Length; i++)
            {
                fallbackParameters[i] = _model.FindParameter(native.FallbackTargets[i].Id);
                fallbackValues[i] = native.FallbackTargets[i].Value;
            }

            return CreatePoseClip(id, fallbackParameters, fallbackValues, -1, autoRestore);
        }

        var poseIndex = FindParamPoseIndex(id);
        if (poseIndex < 0) return null;
        return CreatePoseClip(id, _paramPoseParameters[poseIndex], _paramPoseValues[poseIndex], poseIndex, autoRestore);
    }

    private static Clip CreatePoseClip(string id, CubismParameter?[] parameters, float[] values, int poseIndex,
        double autoRestore)
        => new()
        {
            Id = id,
            PoseIndex = poseIndex,
            PoseParameters = parameters,
            PoseValues = values,
            AutoRestoreSeconds = autoRestore,
            Phase = ClipPhase.Pose,
        };

    /// <summary>从 enter 动作数据提取保持姿势（曲线末值 → 参数引用数组；手势开始才调一次）。</summary>
    private void BuildHoldFromMotion(Clip clip, Motion3Data data)
    {
        data.CollectFinalParameterValues(_finalValueScratch);
        var count = _finalValueScratch.Count;
        clip.HoldParameters = new CubismParameter?[count];
        clip.HoldValues = new float[count];
        var used = 0;
        foreach (var pair in _finalValueScratch)
        {
            clip.HoldParameters[used] = _model.FindParameter(pair.Key);
            clip.HoldValues[used] = pair.Value;
            used++;
        }

        clip.HoldUsed = used;
    }

    private static string? NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var text = id.Trim();
        if (Array.IndexOf(GestureIdList, text) >= 0) return text;
        return text switch
        {
            "hand_on_chest" => IdHandOnChest,
            "point" => IdPoint,
            "grass" => IdGrass,
            "wave" => IdWave,
            "heart" => IdHeart,
            "akimbo" => IdAkimbo,
            "head_hold" => IdHeadHold,
            "hands_down" => IdHandsDown,
            _ => null,
        };
    }

    private static NativeGesture? FindNative(string id)
    {
        foreach (var gesture in NativeGestures)
        {
            if (string.Equals(gesture.Id, id, StringComparison.Ordinal)) return gesture;
        }

        return null;
    }

    private int FindParamPoseIndex(string id)
    {
        // ParamPoseTargets 与 GestureIdList 前三项之后一一对应：举手 / 比心 / 叉腰 / 抱头 / 放下手
        for (var i = 0; i < GestureIdList.Length; i++)
        {
            if (!string.Equals(GestureIdList[i], id, StringComparison.Ordinal)) continue;
            var poseIndex = i - NativeGestures.Length;
            return poseIndex >= 0 && poseIndex < ParamPoseTargets.Length ? poseIndex : -1;
        }

        return -1;
    }
}
