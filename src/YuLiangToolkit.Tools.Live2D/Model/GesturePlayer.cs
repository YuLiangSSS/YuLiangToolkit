using YuLiangToolkit.Tools.Live2D.Interop;

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
/// 手势播放器：作者原生手势（enter/exit 动作 + 保持姿势）+ 参数姿势（直接淡入保持）。
///
/// <para><b>原生手势</b>（同款模型作者留有动作）：先播 enter 动作（<see cref="MotionPlayer"/>，淡入 0.12s）；
/// enter 播完后从 motion 曲线的末值提取「保持姿势」逐帧写入（因此在动作淡出前接住手臂，不会掉回 IDLE）；
/// 切走时播 exit 动作，播完释放。</para>
///
/// <para><b>参数姿势</b>（作者没留动作）：直接给 LArm/LForearm/LWrist/LHand、RArm/... 等参数目标值，
/// 0.35 秒平滑淡入保持；停止时 0.25 秒淡出回默认。数值是保守初值（同款模型 -1~1、默认 0），
/// 若 UI 侧觉得幅度不够可继续微调。</para>
///
/// <para><b>受管参数</b>：只在「这一帧真的写」的参数记录到 touched 下标；运行时下一帧开头复位它们，
/// 手势停掉后不写、手动值即可保留（与 music / performance / dance 同一套机制）。</para>
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
        // 抱头：双臂向外上方抬起 + 微微仰头 / 皱眉（模型手臂贴头幅度有限，靠头颈一起表现）
        new[]
        {
            new PoseTarget("LArm", 1.00f), new PoseTarget("LForearm", -0.60f),
            new PoseTarget("LWrist", -0.80f), new PoseTarget("LHand", -0.20f),
            new PoseTarget("RArm", 1.00f), new PoseTarget("RForearm", -0.60f),
            new PoseTarget("RWrist", 0.80f), new PoseTarget("RHand", -0.20f),
            new PoseTarget("ParamAngleY", 5f), new PoseTarget("BrowInnerUp", 0.30f),
            new PoseTarget("ParamAngleZ", 2f),
        },
        // 放下手：全部回到自然垂手 / 正头
        new[]
        {
            new PoseTarget("LArm", 0.10f), new PoseTarget("LForearm", 0.05f),
            new PoseTarget("LWrist", 0.0f), new PoseTarget("LHand", 0.0f), new PoseTarget("LHand2", 0.0f),
            new PoseTarget("RArm", 0.10f), new PoseTarget("RForearm", 0.05f),
            new PoseTarget("RWrist", 0.0f), new PoseTarget("RHand", 0.0f), new PoseTarget("RHand2", 0.0f),
            new PoseTarget("ShoulderX", 0.0f), new PoseTarget("ShoulderY", 0.0f),
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

    private enum GesturePhase
    {
        None,
        Enter,
        Hold,
        Exit,
        PoseIn,
        PoseHold,
        PoseOut,
    }

    private const double EnterFadeSeconds = 0.12;
    private const double ExitFadeSeconds = 0.12;
    private const double PoseFadeInSeconds = 0.35;
    private const double PoseFadeOutSeconds = 0.25;

    private readonly CubismModel _model;
    private readonly Func<string, MotionEntry?> _findMotionEntry;
    private readonly Func<MotionEntry, Motion3Data?> _findMotionData;
    private readonly Action<MotionEntry, Motion3Data, double> _playMotion;
    private readonly Action<double> _stopMotion;

    /// <summary>参数姿势解析好的目标（加载时一次；参数不存在的目标直接丢掉）。</summary>
    private readonly CubismParameter?[][] _paramPoseParameters;

    private readonly float[][] _paramPoseValues;

    private readonly Dictionary<string, float> _finalValueScratch = new(24);

    private string? _activeId;
    private GesturePhase _phase;
    private double _phaseTimer;
    private double _phaseDuration;

    private CubismParameter?[] _holdParameters = Array.Empty<CubismParameter?>();
    private float[] _holdValues = Array.Empty<float>();

    private CubismParameter?[] _poseParameters = Array.Empty<CubismParameter?>();
    private float[] _poseValues = Array.Empty<float>();
    private float _poseWeight;

    private int[] _touchedIndices = new int[48];
    private int _touchedCount;

    public GesturePlayer(
        CubismModel model,
        Func<string, MotionEntry?> findMotionEntry,
        Func<MotionEntry, Motion3Data?> findMotionData,
        Action<MotionEntry, Motion3Data, double> playMotion,
        Action<double> stopMotion)
    {
        _model = model;
        _findMotionEntry = findMotionEntry;
        _findMotionData = findMotionData;
        _playMotion = playMotion;
        _stopMotion = stopMotion;

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

    public IReadOnlyList<string> Ids => GestureIdList;

    /// <summary>当前手势 id（没有为 null；退出动作播放期间也算 null）。</summary>
    public string? ActiveId => _activeId;

    /// <summary>是否还在播手势（含 enter / hold / 退出 / 参数淡入淡出）。</summary>
    public bool HasActive => _activeId is not null || _phase is GesturePhase.Enter or GesturePhase.Hold or GesturePhase.Exit
        or GesturePhase.PoseIn or GesturePhase.PoseHold or GesturePhase.PoseOut;

    /// <summary>本帧写过的参数个数（运行时下一帧开头复位）。</summary>
    public int TouchedCount => _touchedCount;

    public int GetTouchedIndex(int index) => _touchedIndices[index];

    /// <summary>
    /// 播一个手势（id 用中文名或 jingyu 的英文名都行）。
    /// <paramref name="stopMotionFirst"/>：true = 先停当前动作（用户点手势时用；表演内部触发的 gesture 传 false）。
    /// </summary>
    public bool Play(string id, bool stopMotionFirst)
    {
        var normalized = NormalizeId(id);
        if (normalized is null) return false;
        if (string.Equals(_activeId, normalized, StringComparison.Ordinal)) return true;

        // 换手势：先把手势自身状态清掉（不播退出动作，直接切）
        ReleaseHold();
        _poseWeight = 0;

        var native = FindNative(normalized);
        if (native is not null)
        {
            var entry = _findMotionEntry(native.EnterMotionName);
            var data = entry is null ? null : _findMotionData(entry);
            if (entry is not null && data is not null)
            {
                if (stopMotionFirst) _stopMotion(EnterFadeSeconds);
                BuildHoldFromMotion(data);
                _playMotion(entry, data, EnterFadeSeconds);
                _activeId = normalized;
                _phase = GesturePhase.Enter;
                _phaseTimer = 0;
                _phaseDuration = data.Duration;
                return true;
            }

            // enter 动作缺失 → 参数姿势兜底（直接淡入保持）
            StartPose(normalized, native.FallbackTargets, stopMotionFirst);
            return true;
        }

        var poseIndex = FindParamPoseIndex(normalized);
        if (poseIndex < 0) return false;
        StartPose(normalized, ParamPoseTargets[poseIndex], stopMotionFirst, poseIndex);
        return true;
    }

    /// <summary>停止手势：原生手势播退出动作；参数姿势 0.25 秒淡出回默认。</summary>
    public void Stop()
    {
        if (_activeId is null && _phase == GesturePhase.None) return;
        if (_phase == GesturePhase.Enter || _phase == GesturePhase.Hold)
        {
            var native = FindNative(_activeId!);
            if (native is not null)
            {
                var entry = _findMotionEntry(native.ExitMotionName);
                var data = entry is null ? null : _findMotionData(entry);
                if (entry is not null && data is not null)
                {
                    ReleaseHold();
                    _playMotion(entry, data, ExitFadeSeconds);
                    _activeId = null;
                    _phase = GesturePhase.Exit;
                    _phaseTimer = 0;
                    _phaseDuration = data.Duration;
                    return;
                }
            }

            // 参数姿势 / 兜底：淡出
            _activeId = null;
            _phase = GesturePhase.PoseOut;
            return;
        }

        // 已经在退出：保持退出动作
        if (_phase == GesturePhase.Exit) return;

        // 参数姿势淡入中 / 淡出中：转到淡出
        if (_phase is GesturePhase.PoseIn or GesturePhase.PoseHold)
        {
            _activeId = null;
            _phase = GesturePhase.PoseOut;
        }
        else if (_phase == GesturePhase.PoseOut)
        {
            _activeId = null;
        }
    }

    /// <summary>立即释放（停所有演示时用；不播退出动作）。</summary>
    public void StopImmediate()
    {
        ReleaseHold();
        _poseWeight = 0;
        _activeId = null;
        _phase = GesturePhase.None;
    }

    /// <summary>推进一帧：写保持姿势 / 参数姿势（在动作更新之后调用，覆盖 IDLE 的手臂曲线）。</summary>
    public void Update(double deltaSeconds)
    {
        _touchedCount = 0;
        switch (_phase)
        {
            case GesturePhase.None:
                return;

            case GesturePhase.Enter:
                _phaseTimer += deltaSeconds;
                if (_phaseTimer < _phaseDuration) return;
                _phase = GesturePhase.Hold;
                _phaseTimer = 0;
                return;

            case GesturePhase.Hold:
                WriteHold();
                return;

            case GesturePhase.Exit:
                _phaseTimer += deltaSeconds;
                if (_phaseTimer >= _phaseDuration + ExitFadeSeconds + 0.02) _phase = GesturePhase.None;
                return;

            case GesturePhase.PoseIn:
                _poseWeight += (float)(deltaSeconds / PoseFadeInSeconds);
                if (_poseWeight >= 1)
                {
                    _poseWeight = 1;
                    _phase = GesturePhase.PoseHold;
                }

                WritePose(_poseWeight);
                return;

            case GesturePhase.PoseHold:
                WritePose(1f);
                return;

            case GesturePhase.PoseOut:
                _poseWeight -= (float)(deltaSeconds / PoseFadeOutSeconds);
                if (_poseWeight <= 0)
                {
                    _poseWeight = 0;
                    _phase = GesturePhase.None;
                    return;
                }

                WritePose(_poseWeight);
                return;
        }
    }

    /// <summary>运行时下一帧开头复位「上一帧手势写过的参数」。</summary>
    public void ResetTouched(CubismParameter[] parameters)
    {
        for (var i = 0; i < _touchedCount; i++)
        {
            var parameter = parameters[_touchedIndices[i]];
            parameter.Value = parameter.DefaultValue;
        }

        _touchedCount = 0;
    }

    private void StartPose(string id, PoseTarget[] targets, bool stopMotionFirst, int poseIndex = -1)
    {
        if (stopMotionFirst) _stopMotion(EnterFadeSeconds);
        if (poseIndex >= 0)
        {
            _poseParameters = _paramPoseParameters[poseIndex];
            _poseValues = _paramPoseValues[poseIndex];
        }
        else
        {
            BuildPoseFromTargets(targets);
        }

        _poseWeight = 0;
        _activeId = id;
        _phase = GesturePhase.PoseIn;
        _phaseTimer = 0;
    }

    /// <summary>把参数目标解析成数组（enter 动作缺失时的兜底；手势开始才调一次）。</summary>
    private void BuildPoseFromTargets(PoseTarget[] targets)
    {
        var parameters = new CubismParameter?[targets.Length];
        var values = new float[targets.Length];
        for (var i = 0; i < targets.Length; i++)
        {
            parameters[i] = _model.FindParameter(targets[i].Id);
            values[i] = targets[i].Value;
        }

        _poseParameters = parameters;
        _poseValues = values;
    }

    /// <summary>从 enter 动作数据提取保持姿势（曲线末值 → 参数引用数组；手势开始才调一次）。</summary>
    private void BuildHoldFromMotion(Motion3Data data)
    {
        data.CollectFinalParameterValues(_finalValueScratch);
        var count = _finalValueScratch.Count;
        if (_holdParameters.Length < count)
        {
            _holdParameters = new CubismParameter?[count];
            _holdValues = new float[count];
        }

        var used = 0;
        foreach (var pair in _finalValueScratch)
        {
            _holdParameters[used] = _model.FindParameter(pair.Key);
            _holdValues[used] = pair.Value;
            used++;
        }

        for (var i = used; i < _holdParameters.Length; i++) _holdParameters[i] = null;
        _holdUsed = used;
    }

    private int _holdUsed;

    private void WriteHold()
    {
        for (var i = 0; i < _holdUsed; i++)
        {
            var parameter = _holdParameters[i];
            if (parameter is null) continue;
            parameter.Value = _holdValues[i];
            MarkTouched(parameter);
        }
    }

    private void WritePose(float weight)
    {
        var smooth = DemoMath.SmoothStep(weight);
        for (var i = 0; i < _poseParameters.Length; i++)
        {
            var parameter = _poseParameters[i];
            if (parameter is null) continue;
            parameter.Value = DemoMath.BlendFromDefault(parameter, _poseValues[i], smooth);
            MarkTouched(parameter);
        }
    }

    private void ReleaseHold()
    {
        _holdUsed = 0;
        if (_holdParameters.Length > 0) Array.Clear(_holdParameters);
    }

    private void MarkTouched(CubismParameter parameter)
    {
        if (_touchedCount >= _touchedIndices.Length)
        {
            Array.Resize(ref _touchedIndices, _touchedIndices.Length * 2);
        }

        _touchedIndices[_touchedCount++] = parameter.Index;
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
