using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>时间线表演里的动作类型（对照 jingyu 的 action kinds；这里只做运行时能表达的部分）。</summary>
internal enum PerformanceActionKind
{
    /// <summary>参数姿态：直接给语义参数目标值（按窗口淡入淡出）。</summary>
    Pose,

    /// <summary>看向某方向（眼睛先动、头部跟随）：AngleX/Y + AngleZ 头输入。</summary>
    Look,

    /// <summary>点头：AngleY / ParamAngleY 的阻尼振荡。</summary>
    Nod,

    /// <summary>歪头：AngleZ / ParamAngleZ + 身体反向平衡。</summary>
    Tilt,

    /// <summary>微笑：嘴角 / 眼睛一起变化。</summary>
    Smile,

    /// <summary>眨眼（可单边）。</summary>
    Blink,

    /// <summary>调 <see cref="GesturePlayer"/> 的拇指 / 手势动作。</summary>
    Gesture,

    /// <summary>说话口型节奏（只在没有真实语音 / 动捕时生效）。</summary>
    Talk,
}

/// <summary>pose 动作的参数目标（id 在构造时解析成参数引用，运行时不查字典）。</summary>
internal readonly struct PoseValue
{
    public PoseValue(string id, float value)
    {
        Id = id;
        Value = value;
    }

    public string Id { get; }
    public float Value { get; }
}

internal sealed class PerformanceAction
{
    public PerformanceActionKind Kind;
    public float At;
    public float Duration;
    public float Strength = 0.5f;

    /// <summary>Look：0=center、1=left、2=right、3=up、4=down；Tilt：0=left、1=right。</summary>
    public int Direction;

    public int Count = 1;

    /// <summary>Blink：0=both、1=left、2=right。</summary>
    public int Side;

    /// <summary>Gesture：手势 id（中文名或 jingyu 英文名）。</summary>
    public string? GestureId;

    /// <summary>Pose：参数目标。</summary>
    public PoseValue[] Values = Array.Empty<PoseValue>();

    /// <summary>Pose 目标解析后的参数引用（构造时填）。</summary>
    public CubismParameter?[]? Resolved;

    public bool GestureStarted;
}

internal sealed class PerformanceDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required float Duration { get; init; }
    public required PerformanceAction[] Actions { get; init; }
}

/// <summary>
/// 时间线表演播放器（jingyu 风格）：每个表演 = 一串带 at / duration 的动作（pose/look/nod/tilt/smile/blink/gesture/talk），
/// 按时间用 smoothstep 淡入淡出采样写参数；结束后自动回默认并释放（受管参数由运行时下一帧复位）。
///
/// <para><b>0 分配</b>：动作表与参数引用都在构造时准备好；每帧只做循环 + 写参数，不 new。</para>
/// <para><b>嘴型优先级</b>：talk 只在 <c>allowTalk</c>（没有真实语音、没有动捕脸）时写，
/// 不抢 <see cref="ModelRuntime.SpeechActive"/> 的语音口型。</para>
/// </summary>
internal sealed class PerformancePlayer
{
    private const float PoseFadeSeconds = 0.30f;
    private const float TalkFadeSeconds = 0.25f;

    private readonly CubismModel _model;
    private readonly Action<string> _startGesture;
    private readonly Action _stopGesture;
    private readonly PerformanceDefinition[] _definitions;
    private readonly string[] _ids;

    private readonly CubismParameter? _mouthOpen;
    private readonly CubismParameter? _mouthForm;
    private readonly CubismParameter? _jawOpen;

    private int[] _touchedIndices = new int[64];
    private int _touchedCount;

    private PerformanceDefinition? _active;
    private double _elapsed;
    private bool _gestureStarted;

    public PerformancePlayer(CubismModel model, Action<string> startGesture, Action stopGesture)
    {
        _model = model;
        _startGesture = startGesture;
        _stopGesture = stopGesture;
        _mouthOpen = model.FindParameter("ParamMouthOpenY");
        _mouthForm = model.FindParameter("ParamMouthForm");
        _jawOpen = model.FindParameter("JawOpen");
        _definitions = BuildDefinitions();
        _ids = new string[_definitions.Length];
        for (var i = 0; i < _definitions.Length; i++) _ids[i] = _definitions[i].Id;
        ResolveTargets();
    }

    public IReadOnlyList<PerformanceDefinition> Definitions => _definitions;

    /// <summary>所有表演 id（= 中文名）。</summary>
    public IReadOnlyList<string> Ids => _ids;

    /// <summary>当前表演 id（没有为 null）。</summary>
    public string? ActiveId => _active?.Id;

    public bool HasActive => _active is not null;

    /// <summary>当前表演总时长（秒；没有为 0）。</summary>
    public double ActiveDuration => _active?.Duration ?? 0;

    /// <summary>当前表演已播放时间（秒）。</summary>
    public double ElapsedSeconds => _elapsed;

    /// <summary>本帧写过的参数个数（运行时下一帧开头复位）。</summary>
    public int TouchedCount => _touchedCount;

    public int GetTouchedIndex(int index) => _touchedIndices[index];

    /// <summary>播一个表演（未知名返回 false；会先把上一个表演复位到时间 0）。</summary>
    public bool Play(string id)
    {
        var definition = Find(id);
        if (definition is null) return false;
        StopGestureIfAny();
        _active = definition;
        _elapsed = 0;
        for (var i = 0; i < definition.Actions.Length; i++) definition.Actions[i].GestureStarted = false;
        return true;
    }

    /// <summary>立即停止（不走完时间线；已触发的 gesture 会停下）。</summary>
    public void Stop()
    {
        StopGestureIfAny();
        _active = null;
        _elapsed = 0;
    }

    /// <summary>推进一帧（在动作更新之后、表情 / 物理之前调用）。</summary>
    public void Update(double deltaSeconds, bool allowTalk)
    {
        _touchedCount = 0;
        var definition = _active;
        if (definition is null) return;

        _elapsed += deltaSeconds;
        if (_elapsed >= definition.Duration)
        {
            StopGestureIfAny();
            _active = null;
            _elapsed = 0;
            return;
        }

        var actions = definition.Actions;
        for (var i = 0; i < actions.Length; i++)
        {
            var action = actions[i];
            var local = (float)(_elapsed - action.At);
            if (local < 0) continue;
            if (local > action.Duration)
            {
                if (action.Kind == PerformanceActionKind.Gesture && action.GestureStarted)
                {
                    action.GestureStarted = false;
                    _gestureStarted = false;
                    _stopGesture();
                }

                continue;
            }

            switch (action.Kind)
            {
                case PerformanceActionKind.Pose:
                    ApplyPose(action, local);
                    break;
                case PerformanceActionKind.Look:
                    ApplyLook(action, local);
                    break;
                case PerformanceActionKind.Nod:
                    ApplyNod(action, local);
                    break;
                case PerformanceActionKind.Tilt:
                    ApplyTilt(action, local);
                    break;
                case PerformanceActionKind.Smile:
                    ApplySmile(action, local);
                    break;
                case PerformanceActionKind.Blink:
                    ApplyBlink(action, local);
                    break;
                case PerformanceActionKind.Talk:
                    if (allowTalk) ApplyTalk(action, local);
                    break;
                case PerformanceActionKind.Gesture:
                    ApplyGesture(action, local);
                    break;
            }
        }
    }

    /// <summary>运行时下一帧开头复位「上一帧表演写过的参数」。</summary>
    public void ResetTouched(CubismParameter[] parameters)
    {
        for (var i = 0; i < _touchedCount; i++)
        {
            var parameter = parameters[_touchedIndices[i]];
            parameter.Value = parameter.DefaultValue;
        }

        _touchedCount = 0;
    }

    // ---------------- 各类动作的采样 ----------------

    private void ApplyPose(PerformanceAction action, float local)
    {
        var resolved = action.Resolved;
        if (resolved is null) return;
        var weight = Envelope(local, action.Duration, 0.28f, 0.35f);
        if (weight <= 0) return;
        var smooth = DemoMath.SmoothStep(weight);
        for (var i = 0; i < resolved.Length; i++)
        {
            var parameter = resolved[i];
            if (parameter is null) continue;
            WriteBlended(parameter, action.Values[i].Value, smooth);
        }
    }

    private void ApplyLook(PerformanceAction action, float local)
    {
        var weight = Envelope(local, action.Duration, 0.22f, 0.30f);
        if (weight <= 0) return;
        var smooth = DemoMath.SmoothStep(weight);
        var strength = action.Strength;
        float angleX = 0;
        float angleY = 0;
        float eyeX = 0;
        float eyeY = 0;
        switch (action.Direction)
        {
            case 1: // 左（观众屏幕左）
                angleX = -9f * strength;
                eyeX = -0.75f * strength;
                break;
            case 2:
                angleX = 9f * strength;
                eyeX = 0.75f * strength;
                break;
            case 3: // 上
                angleY = 7f * strength;
                eyeY = 0.65f * strength;
                break;
            case 4:
                angleY = -7f * strength;
                eyeY = -0.65f * strength;
                break;
        }

        Write("AngleX", angleX * smooth);
        Write("AngleY", angleY * smooth);
        Write("ParamAngleX", angleX * 0.8f * smooth);
        Write("ParamAngleY", angleY * 0.8f * smooth);
        Write("ParamEyeBallX", eyeX * smooth);
        Write("ParamEyeBallY", eyeY * smooth);
    }

    private void ApplyNod(PerformanceAction action, float local)
    {
        var weight = Envelope(local, action.Duration, 0.18f, 0.28f);
        if (weight <= 0) return;
        var smooth = DemoMath.SmoothStep(weight);
        var count = Math.Clamp(action.Count, 1, 4);
        var phase = local / action.Duration;
        var swing = Math.Sin(2.0 * Math.PI * count * phase);
        var angle = (float)(-6.0 * action.Strength * swing * smooth);
        Write("AngleY", angle);
        Write("ParamAngleY", angle * 0.9f);
        Write("AngleX", (float)(1.2 * action.Strength * Math.Sin(Math.PI * phase) * smooth));
    }

    private void ApplyTilt(PerformanceAction action, float local)
    {
        var weight = Envelope(local, action.Duration, 0.22f, 0.30f);
        if (weight <= 0) return;
        var smooth = DemoMath.SmoothStep(weight);
        var sign = action.Direction == 1 ? -1f : 1f;
        var angle = 8f * action.Strength * sign * smooth;
        Write("AngleZ", angle);
        Write("ParamAngleZ", angle * 0.9f);
        Write("PositionX", -2.2f * action.Strength * sign * smooth);
    }

    private void ApplySmile(PerformanceAction action, float local)
    {
        var weight = Envelope(local, action.Duration, 0.28f, 0.35f);
        if (weight <= 0) return;
        var smooth = DemoMath.SmoothStep(weight);
        var strength = action.Strength;
        Write("EyeSmile", 0.85f * strength * smooth);
        Write("MouthSmile_L", 0.85f * strength * smooth);
        Write("MouthSmile_R", 0.85f * strength * smooth);
        Write("ParamMouthForm", 0.45f * strength * smooth);
    }

    private void ApplyBlink(PerformanceAction action, float local)
    {
        var weight = Envelope(local, action.Duration, 0.08f, 0.12f);
        if (weight <= 0) return;
        var pulse = Math.Sin(Math.PI * Clamp01(local / action.Duration));
        var amount = (float)(pulse * weight * action.Strength);
        var close = 1f - amount;
        if (action.Side is 0 or 1) Write("ParamEyeLOpen", close);
        if (action.Side is 0 or 2) Write("ParamEyeROpen", close);
    }

    private void ApplyTalk(PerformanceAction action, float local)
    {
        var weight = Envelope(local, action.Duration, TalkFadeSeconds, TalkFadeSeconds);
        if (weight <= 0) return;
        var smooth = DemoMath.SmoothStep(weight);
        var rhythm = 0.5 + (0.5 * Math.Sin(2.0 * Math.PI * 4.2 * local));
        var open = (float)(action.Strength * 0.35 * rhythm);
        Write(_mouthOpen, open * smooth);
        Write(_mouthForm, 0.10f * smooth);
        Write(_jawOpen, open * 0.6f * smooth);
    }

    private void ApplyGesture(PerformanceAction action, float local)
    {
        if (!action.GestureStarted && action.GestureId is not null)
        {
            action.GestureStarted = true;
            _gestureStarted = true;
            _startGesture(action.GestureId);
        }
    }

    private void StopGestureIfAny()
    {
        if (!_gestureStarted) return;
        _gestureStarted = false;
        _stopGesture();
        var actions = _active?.Actions;
        if (actions is null) return;
        for (var i = 0; i < actions.Length; i++) actions[i].GestureStarted = false;
    }

    // ---------------- 工具 ----------------

    /// <summary>窗口包络：两边 smoothstep 淡入淡出，返回 0~1（再交给 SmoothStep 平滑）。</summary>
    private static float Envelope(float local, float duration, float fadeIn, float fadeOut)
    {
        if (duration <= 0) return 0;
        if (fadeIn > duration * 0.45f) fadeIn = duration * 0.45f;
        if (fadeOut > duration * 0.45f) fadeOut = duration * 0.45f;
        var inWeight = fadeIn <= 0 ? 1f : Clamp01(local / fadeIn);
        var outWeight = fadeOut <= 0 ? 1f : Clamp01((duration - local) / fadeOut);
        return Math.Min(inWeight, outWeight);
    }

    private static float Clamp01(float value) => value < 0 ? 0 : value > 1 ? 1 : value;

    /// <summary>pose 写入：参数值 = 默认值 + (目标值 - 默认值) × 权重（平滑回正）。</summary>
    private void WriteBlended(CubismParameter parameter, float target, float weight)
    {
        parameter.Value = DemoMath.BlendFromDefault(parameter, target, weight);
        MarkTouched(parameter);
    }

    private void Write(string id, float value) => Write(_model.FindParameter(id), value);

    private void Write(CubismParameter? parameter, double value)
    {
        if (parameter is null) return;
        parameter.Value = (float)value;
        MarkTouched(parameter);
    }

    private void MarkTouched(CubismParameter parameter)
    {
        if (_touchedCount >= _touchedIndices.Length)
        {
            Array.Resize(ref _touchedIndices, _touchedIndices.Length * 2);
        }

        _touchedIndices[_touchedCount++] = parameter.Index;
    }

    private PerformanceDefinition? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var text = id.Trim();
        foreach (var definition in _definitions)
        {
            if (string.Equals(definition.Id, text, StringComparison.Ordinal) ||
                string.Equals(definition.DisplayName, text, StringComparison.Ordinal))
            {
                return definition;
            }
        }

        return null;
    }

    private void ResolveTargets()
    {
        foreach (var definition in _definitions)
        {
            foreach (var action in definition.Actions)
            {
                if (action.Kind != PerformanceActionKind.Pose) continue;
                var resolved = new CubismParameter?[action.Values.Length];
                for (var i = 0; i < action.Values.Length; i++) resolved[i] = _model.FindParameter(action.Values[i].Id);
                action.Resolved = resolved;
            }
        }
    }

    // ---------------- 内置表演数据（中文 id = 显示名；参数值参考 jingyu 语义输入） ----------------

    private static PerformanceDefinition[] BuildDefinitions() => new[]
    {
        new PerformanceDefinition
        {
            Id = "好奇→思考→恍然大悟",
            DisplayName = "好奇→思考→恍然大悟",
            Duration = 15.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Look, 0f, 2.4f, strength: 0.65f, direction: 1),
                A(PerformanceActionKind.Tilt, 0.4f, 3.0f, strength: 0.55f, direction: 1),
                A(PerformanceActionKind.Pose, 0.3f, 3.6f, values: new[]
                {
                    new PoseValue("ParamBrowLY", 0.55f), new PoseValue("ParamBrowRY", -0.15f),
                    new PoseValue("BrowInnerUp", 0.40f), new PoseValue("ParamMouthForm", -0.20f),
                }),
                A(PerformanceActionKind.Look, 3.6f, 2.4f, strength: 0.55f, direction: 2),
                A(PerformanceActionKind.Pose, 5.0f, 3.0f, values: new[]
                {
                    new PoseValue("ParamEyeLOpen", 0.70f), new PoseValue("ParamEyeROpen", 0.70f),
                    new PoseValue("ParamEyeBallX", 0.35f), new PoseValue("AngleZ", -6f),
                }),
                A(PerformanceActionKind.Nod, 8.2f, 2.0f, strength: 0.55f, count: 2),
                A(PerformanceActionKind.Pose, 8.6f, 1.6f, values: new[]
                {
                    new PoseValue("ParamBrowLY", 0.6f), new PoseValue("ParamBrowRY", 0.6f),
                    new PoseValue("ParamEyeLOpen", 1.2f), new PoseValue("ParamEyeROpen", 1.2f),
                    new PoseValue("ParamMouthOpenY", 0.28f),
                }),
                A(PerformanceActionKind.Smile, 9.8f, 4.6f, strength: 0.8f),
                A(PerformanceActionKind.Talk, 10.4f, 3.4f, strength: 0.6f),
                A(PerformanceActionKind.Tilt, 11.4f, 2.2f, strength: 0.35f, direction: 0),
            },
        },
        new PerformanceDefinition
        {
            Id = "俏皮比心",
            DisplayName = "俏皮比心",
            Duration = 10.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Gesture, 0f, 6.2f, gestureId: GesturePlayer.IdHeart),
                A(PerformanceActionKind.Smile, 0.4f, 6.4f, strength: 0.85f),
                A(PerformanceActionKind.Tilt, 0.8f, 3.0f, strength: 0.6f, direction: 1),
                A(PerformanceActionKind.Look, 0f, 2.0f, strength: 0.4f, direction: 0),
                A(PerformanceActionKind.Nod, 6.6f, 2.2f, strength: 0.45f, count: 1),
                A(PerformanceActionKind.Blink, 4.8f, 0.8f, strength: 1.0f, side: 2),
            },
        },
        new PerformanceDefinition
        {
            Id = "开心挥手",
            DisplayName = "开心挥手",
            Duration = 9.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Gesture, 0f, 6.4f, gestureId: GesturePlayer.IdWave),
                A(PerformanceActionKind.Smile, 0.3f, 7.0f, strength: 0.9f),
                A(PerformanceActionKind.Tilt, 0.6f, 2.6f, strength: 0.5f, direction: 1),
                A(PerformanceActionKind.Tilt, 3.4f, 2.6f, strength: 0.45f, direction: 0),
                A(PerformanceActionKind.Nod, 6.6f, 1.8f, strength: 0.5f, count: 2),
                A(PerformanceActionKind.Blink, 7.6f, 0.7f, side: 0),
            },
        },
        new PerformanceDefinition
        {
            Id = "骄傲展示",
            DisplayName = "骄傲展示",
            Duration = 12.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Pose, 0.2f, 4.0f, values: new[]
                {
                    new PoseValue("ParamBrowLY", 0.45f), new PoseValue("ParamBrowRY", 0.45f),
                    new PoseValue("AngleX", -4f), new PoseValue("AngleZ", 4f),
                    new PoseValue("PositionZ", -3f), new PoseValue("CheeckPuff", 0.0f),
                }),
                A(PerformanceActionKind.Smile, 1.0f, 6.0f, strength: 0.75f),
                A(PerformanceActionKind.Gesture, 3.0f, 6.4f, gestureId: GesturePlayer.IdGrass),
                A(PerformanceActionKind.Look, 3.4f, 2.6f, strength: 0.5f, direction: 3),
                A(PerformanceActionKind.Nod, 8.6f, 2.2f, strength: 0.55f, count: 2),
                A(PerformanceActionKind.Talk, 9.4f, 2.0f, strength: 0.35f),
            },
        },
        new PerformanceDefinition
        {
            Id = "害羞低头",
            DisplayName = "害羞低头",
            Duration = 10.5f,
            Actions = new[]
            {
                A(PerformanceActionKind.Look, 0f, 5.0f, strength: 0.55f, direction: 4),
                A(PerformanceActionKind.Tilt, 1.0f, 4.0f, strength: 0.4f, direction: 0),
                A(PerformanceActionKind.Pose, 0.4f, 5.0f, values: new[]
                {
                    new PoseValue("ParamBrowLY", -0.25f), new PoseValue("ParamBrowRY", -0.25f),
                    new PoseValue("BrowInnerUp", 0.45f), new PoseValue("ParamMouthForm", -0.30f),
                }),
                A(PerformanceActionKind.Smile, 0.8f, 4.4f, strength: 0.3f),
                A(PerformanceActionKind.Blink, 1.6f, 0.8f, side: 0),
                A(PerformanceActionKind.Talk, 6.2f, 1.8f, strength: 0.25f),
            },
        },
        new PerformanceDefinition
        {
            Id = "晚安道别",
            DisplayName = "晚安道别",
            Duration = 11.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Gesture, 0f, 6.6f, gestureId: GesturePlayer.IdWave),
                A(PerformanceActionKind.Nod, 0.8f, 2.4f, strength: 0.5f, count: 1),
                A(PerformanceActionKind.Smile, 1.2f, 7.0f, strength: 0.7f),
                A(PerformanceActionKind.Look, 0f, 2.4f, strength: 0.5f, direction: 0),
                A(PerformanceActionKind.Talk, 6.0f, 2.4f, strength: 0.5f),
                A(PerformanceActionKind.Blink, 8.6f, 1.0f, side: 0),
                A(PerformanceActionKind.Tilt, 9.0f, 2.0f, strength: 0.4f, direction: 1),
            },
        },
    };

    private static PerformanceAction A(PerformanceActionKind kind, float at, float duration,
        float strength = 0.5f, int direction = 0, int count = 1, int side = 0, string? gestureId = null,
        PoseValue[]? values = null) => new()
    {
        Kind = kind,
        At = at,
        Duration = duration,
        Strength = strength,
        Direction = direction,
        Count = count,
        Side = side,
        GestureId = gestureId,
        Values = values ?? Array.Empty<PoseValue>(),
    };
}
