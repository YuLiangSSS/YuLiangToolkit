using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>时间线表演里的动作类型。</summary>
internal enum PerformanceActionKind
{
    /// <summary>参数姿态：直接给语义参数目标值（按窗口淡入淡出）。身体轴 / 腰 / 头 / 眼 / 嘴都能写。</summary>
    Pose,

    /// <summary>看向某方向（眼睛先动、头部跟随）：AngleX/Y + AngleZ 头输入。</summary>
    Look,

    /// <summary>点头：AngleY / ParamAngleY 的阻尼振荡。</summary>
    Nod,

    /// <summary>歪头：AngleZ / ParamAngleZ + 身体反向平衡。</summary>
    Tilt,

    /// <summary>微笑：眯眼笑 + 嘴形上扬（MouthSmile_L/R 实测无效，由 ParamMouthForm 表现）。</summary>
    Smile,

    /// <summary>眨眼（可单边）。</summary>
    Blink,

    /// <summary>调 <see cref="GesturePlayer"/> 的原生手势（抱胸致意 / 竖起食指 / 拿起狗尾草）。</summary>
    Gesture,

    /// <summary>说话口型节奏（只在没有真实语音 / 动捕时生效）。</summary>
    Talk,

    /// <summary>
    /// 挂 / 摘 exp3 表情或装饰（爱心眼 / 脸红 / 黑脸 / 屑屑眼 / 圈圈眼 / ＞＜ / 流泪 /
    /// 单片眼镜 / 黑框眼镜 / 钢盆 / 体型切换…）：At 时刻淡入挂上，动作窗口结束淡出摘掉。
    /// </summary>
    Expression,
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

    /// <summary>Gesture：手势 id（中文名或英文名）。</summary>
    public string? GestureId;

    /// <summary>Expression：要挂的 exp3 表情 / 装饰名（可多个）。</summary>
    public string[] Expressions = Array.Empty<string>();

    /// <summary>Pose：参数目标。</summary>
    public PoseValue[] Values = Array.Empty<PoseValue>();

    /// <summary>Pose 目标解析后的参数引用（构造时填）。</summary>
    public CubismParameter?[]? Resolved;

    public bool GestureStarted;
    public bool ExpressionStarted;
}

internal sealed class PerformanceDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required float Duration { get; init; }
    public required PerformanceAction[] Actions { get; init; }
}

/// <summary>
/// 时间线表演播放器：每个表演 = 一串带 at / duration 的动作
/// （pose / look / nod / tilt / smile / blink / gesture / talk / expression），
/// 按时间用 smoothstep 淡入淡出采样写参数；结束后自动回默认并释放（位姿合成层负责交回）。
///
/// <para><b>2026-11-27 全套重编</b>：旧的 6 个表演大多只有头 / 眼在动（手臂参数画面上看不出来），
/// 参考作者原生动作「脱捕」（13 秒全身：头 + 面部 + 眼嘴 + 手臂 + 身体转动）重新编排了 10 个
/// 完整表演——身体主轴 / 腰 / 位移大幅参与，嘴巴有节奏地动，并大量联动表情 / 装饰（exp3）。</para>
///
/// <para><b>0 分配</b>：动作表与参数引用都在构造时准备好；每帧只做循环 + 写参数，不 new。</para>
/// <para><b>嘴型优先级</b>：talk 只在 <c>allowTalk</c>（没有真实语音、没有动捕脸）时写，
/// 不抢语音口型。</para>
/// </summary>
internal sealed class PerformancePlayer
{
    private const float PoseFadeSeconds = 0.30f;
    private const float TalkFadeSeconds = 0.25f;

    private readonly CubismModel _model;
    private readonly Action<string> _startGesture;
    private readonly Action _stopGesture;
    private readonly Func<string, ExpressionEntry?> _findExpression;
    private readonly Action<ExpressionEntry, double> _applyExpression;
    private readonly Action<ExpressionEntry, double> _removeExpression;
    private readonly PerformanceDefinition[] _definitions;
    private readonly string[] _ids;

    private readonly CubismParameter? _mouthOpen;
    private readonly CubismParameter? _mouthForm;
    private readonly CubismParameter? _jawOpen;

    private PerformanceDefinition? _active;
    private double _elapsed;
    private bool _gestureStarted;

    /// <summary>本段表演挂上的表情 / 装饰（结束时摘掉）。</summary>
    private readonly List<ExpressionEntry> _appliedExpressions = new(4);

    public PerformancePlayer(CubismModel model, Action<string> startGesture, Action stopGesture,
        Func<string, ExpressionEntry?> findExpression,
        Action<ExpressionEntry, double> applyExpression,
        Action<ExpressionEntry, double> removeExpression)
    {
        _model = model;
        _startGesture = startGesture;
        _stopGesture = stopGesture;
        _findExpression = findExpression;
        _applyExpression = applyExpression;
        _removeExpression = removeExpression;
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

    /// <summary>播一个表演（未知名返回 false；会先把上一个表演复位到时间 0）。</summary>
    public bool Play(string id)
    {
        var definition = Find(id);
        if (definition is null) return false;
        StopGestureIfAny();
        ClearAppliedExpressions();
        _active = definition;
        _elapsed = 0;
        foreach (var action in definition.Actions)
        {
            action.GestureStarted = false;
            action.ExpressionStarted = false;
        }

        return true;
    }

    /// <summary>立即停止（不走完时间线；已触发的 gesture 会停下、挂的表情摘掉）。</summary>
    public void Stop()
    {
        StopGestureIfAny();
        ClearAppliedExpressions();
        _active = null;
        _elapsed = 0;
    }

    /// <summary>推进一帧（在位姿合成期、本层的捕获里调用）。</summary>
    public void Update(double deltaSeconds, bool allowTalk)
    {
        var definition = _active;
        if (definition is null) return;

        _elapsed += deltaSeconds;
        if (_elapsed >= definition.Duration)
        {
            StopGestureIfAny();
            ClearAppliedExpressions();
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
                else if (action.Kind == PerformanceActionKind.Expression && action.ExpressionStarted)
                {
                    action.ExpressionStarted = false;
                    RemoveExpressions(action.Expressions);
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
                    ApplyGesture(action);
                    break;
                case PerformanceActionKind.Expression:
                    ApplyExpressionAction(action);
                    break;
            }
        }
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
        // MouthSmile_L/R 实测无效已移除（嘴角上扬靠 ParamMouthForm 表现，眯眼笑靠 EyeSmile）
        Write("EyeSmile", 0.85f * strength * smooth);
        Write("ParamMouthForm", 0.55f * strength * smooth);
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

    private void ApplyGesture(PerformanceAction action)
    {
        if (!action.GestureStarted && action.GestureId is not null)
        {
            action.GestureStarted = true;
            _gestureStarted = true;
            _startGesture(action.GestureId);
        }
    }

    private void ApplyExpressionAction(PerformanceAction action)
    {
        if (action.ExpressionStarted || action.Expressions.Length == 0) return;
        action.ExpressionStarted = true;
        foreach (var name in action.Expressions)
        {
            var entry = _findExpression(name);
            if (entry is null) continue;
            _applyExpression(entry, 0.4);
            _appliedExpressions.Add(entry);
        }
    }

    private void RemoveExpressions(string[] names)
    {
        foreach (var name in names)
        {
            var entry = _findExpression(name);
            if (entry is null) continue;
            _removeExpression(entry, 0.5);
            _appliedExpressions.RemoveAll(item => ReferenceEquals(item, entry));
        }
    }

    private void ClearAppliedExpressions()
    {
        foreach (var entry in _appliedExpressions) _removeExpression(entry, 0.5);
        _appliedExpressions.Clear();
        var active = _active;
        if (active is null) return;
        foreach (var action in active.Actions) action.ExpressionStarted = false;
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
        => parameter.Value = DemoMath.BlendFromDefault(parameter, target, weight);

    private void Write(string id, float value) => Write(_model.FindParameter(id), value);

    private void Write(CubismParameter? parameter, double value)
    {
        if (parameter is null) return;
        parameter.Value = (float)value;
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

    // ---------------- 内置表演数据（2026-11-27 全套重编） ----------------
    //
    // 编排原则（参考作者原生动作「脱捕」的全身感）：
    //   - 身体主轴 ParamBodyAngleX/Y/Z、腰 HipAngleX、位移 PositionX/Y 是主力（画面上看得见的大变化）；
    //   - 头 AngleX/Y/Z + ParamAngleX/Y/Z 跟随 / 反向；眼睛 / 眉毛 / 嘴（张合 + 嘴形 + 舌头）跟上；
    //   - 大量联动 exp3 表情 / 装饰（爱心眼 / 脸红 / 黑脸 / 屑屑眼 / 圈圈眼 / ＞＜ / 流泪 /
    //     单片眼镜 / 黑框眼镜 / 钢盆 / 体型切换）；
    //   - 手臂参数（LArm 等）画面上看不出变化，一律不用；MouthSmile_L/R / ShoulderX / 翅膀等
    //     实测无效参数一律不用（参数有效性实测会把它们拦下）。
    // 手势动作用的都是作者原生手势（抱胸致意 / 竖起食指 / 拿起狗尾草）。

    private static PerformanceDefinition[] BuildDefinitions() => new[]
    {
        new PerformanceDefinition
        {
            Id = "元气早安",
            DisplayName = "元气早安",
            Duration = 12.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Expression, 0f, 3.2f, expressions: new[] { "爱心眼", "脸红" }),
                A(PerformanceActionKind.Look, 0f, 2.2f, strength: 0.7f, direction: 3),
                A(PerformanceActionKind.Pose, 0.2f, 2.8f, values: new[]
                {
                    new PoseValue("ParamBodyAngleY", 5f), new PoseValue("PositionY", 2f),
                    new PoseValue("ParamAngleY", 4f),
                }),
                A(PerformanceActionKind.Talk, 1.2f, 3.0f, strength: 0.75f),
                A(PerformanceActionKind.Pose, 3.4f, 3.6f, values: new[]
                {
                    new PoseValue("ParamBodyAngleX", 6f), new PoseValue("PositionX", 3f),
                    new PoseValue("AngleZ", 4f), new PoseValue("ParamAngleZ", 3f),
                }),
                A(PerformanceActionKind.Nod, 4.2f, 2.4f, strength: 0.7f, count: 2),
                A(PerformanceActionKind.Pose, 7.2f, 3.4f, values: new[]
                {
                    new PoseValue("ParamBodyAngleX", -6f), new PoseValue("PositionX", -3f),
                    new PoseValue("AngleZ", -4f), new PoseValue("ParamAngleZ", -3f),
                }),
                A(PerformanceActionKind.Talk, 7.6f, 2.4f, strength: 0.6f),
                A(PerformanceActionKind.Blink, 8.4f, 0.7f, side: 0),
                A(PerformanceActionKind.Smile, 9.2f, 2.6f, strength: 0.85f),
                A(PerformanceActionKind.Nod, 10.2f, 1.4f, strength: 0.5f, count: 1),
            },
        },
        new PerformanceDefinition
        {
            Id = "开心蹦跳",
            DisplayName = "开心蹦跳",
            Duration = 11.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Expression, 0f, 9.6f, expressions: new[] { "爱心眼" }),
                A(PerformanceActionKind.Pose, 0f, 3.2f, values: new[]
                {
                    new PoseValue("ParamBodyAngleY", 7f), new PoseValue("PositionY", 2.5f),
                    new PoseValue("ParamMouthOpenY", 0.6f), new PoseValue("JawOpen", 0.4f),
                }),
                A(PerformanceActionKind.Pose, 3.4f, 3.2f, values: new[]
                {
                    new PoseValue("ParamBodyAngleY", -7f), new PoseValue("PositionY", -2.5f),
                    new PoseValue("ParamAngleY", 5f),
                }),
                A(PerformanceActionKind.Pose, 6.8f, 2.6f, values: new[]
                {
                    new PoseValue("ParamBodyAngleX", 5f), new PoseValue("PositionX", 2.5f),
                    new PoseValue("ParamBodyAngleZ", 3f),
                }),
                A(PerformanceActionKind.Talk, 3.6f, 5.6f, strength: 0.8f),
                A(PerformanceActionKind.Nod, 6.0f, 2.0f, strength: 0.8f, count: 2),
                A(PerformanceActionKind.Smile, 8.0f, 2.8f, strength: 0.9f),
                A(PerformanceActionKind.Blink, 9.4f, 0.8f, side: 2),
            },
        },
        new PerformanceDefinition
        {
            Id = "好奇打量",
            DisplayName = "好奇打量",
            Duration = 13.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Expression, 0.4f, 3.6f, expressions: new[] { "圈圈眼" }),
                A(PerformanceActionKind.Pose, 0f, 3.0f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", 6f), new PoseValue("AngleZ", 8f),
                    new PoseValue("ParamAngleZ", 6f), new PoseValue("ParamBrowLY", 0.5f),
                    new PoseValue("ParamBrowRY", 0.5f),
                }),
                A(PerformanceActionKind.Look, 0.4f, 2.4f, strength: 0.8f, direction: 1),
                A(PerformanceActionKind.Pose, 3.8f, 3.2f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", -6f), new PoseValue("AngleZ", -8f),
                    new PoseValue("ParamAngleZ", -6f), new PoseValue("ParamBrowLY", 0.35f),
                    new PoseValue("ParamBrowRY", 0.35f), new PoseValue("ParamMouthForm", -0.25f),
                }),
                A(PerformanceActionKind.Look, 4.0f, 2.6f, strength: 0.8f, direction: 2),
                A(PerformanceActionKind.Talk, 6.6f, 2.8f, strength: 0.4f),
                A(PerformanceActionKind.Expression, 7.4f, 4.6f, expressions: new[] { "屑屑眼" }),
                A(PerformanceActionKind.Pose, 7.6f, 4.4f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", 5f), new PoseValue("AngleZ", 7f),
                    new PoseValue("ParamAngleZ", 5f), new PoseValue("ParamAngleY", -3f),
                }),
                A(PerformanceActionKind.Nod, 11.0f, 1.6f, strength: 0.5f, count: 1),
            },
        },
        new PerformanceDefinition
        {
            Id = "傲娇别扭",
            DisplayName = "傲娇别扭",
            Duration = 13.5f,
            Actions = new[]
            {
                A(PerformanceActionKind.Expression, 0f, 5.6f, expressions: new[] { "黑脸" }),
                A(PerformanceActionKind.Pose, 0.2f, 4.8f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", 9f), new PoseValue("AngleZ", -6f),
                    new PoseValue("ParamAngleZ", 4f), new PoseValue("ParamMouthForm", -0.6f),
                    new PoseValue("PositionX", -2.5f),
                }),
                A(PerformanceActionKind.Look, 0.6f, 2.0f, strength: 0.4f, direction: 4),
                A(PerformanceActionKind.Pose, 5.8f, 4.0f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", -9f), new PoseValue("AngleZ", 6f),
                    new PoseValue("ParamAngleZ", -4f), new PoseValue("PositionX", 2.5f),
                }),
                A(PerformanceActionKind.Expression, 5.8f, 6.6f, expressions: new[] { "屑屑眼" }),
                A(PerformanceActionKind.Look, 6.2f, 3.4f, strength: 0.9f, direction: 1),
                A(PerformanceActionKind.Talk, 9.8f, 2.8f, strength: 0.45f),
                A(PerformanceActionKind.Pose, 10.0f, 3.2f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", 6f), new PoseValue("AngleZ", -5f),
                    new PoseValue("ParamAngleZ", 3f), new PoseValue("ParamMouthForm", -0.45f),
                }),
                A(PerformanceActionKind.Blink, 11.4f, 0.7f, side: 1),
            },
        },
        new PerformanceDefinition
        {
            Id = "害羞心动",
            DisplayName = "害羞心动",
            Duration = 11.5f,
            Actions = new[]
            {
                A(PerformanceActionKind.Expression, 0f, 10.4f, expressions: new[] { "爱心眼", "脸红" }),
                A(PerformanceActionKind.Pose, 0f, 4.4f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", 4f), new PoseValue("AngleY", -5f),
                    new PoseValue("ParamAngleY", -4f), new PoseValue("ParamMouthForm", 0.3f),
                }),
                A(PerformanceActionKind.Look, 0.4f, 2.2f, strength: 0.45f, direction: 4),
                A(PerformanceActionKind.Pose, 4.6f, 4.4f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", -4f), new PoseValue("AngleZ", 3f),
                    new PoseValue("ParamAngleZ", -3f), new PoseValue("ParamMouthOpenY", 0.25f),
                }),
                A(PerformanceActionKind.Talk, 5.0f, 3.4f, strength: 0.4f),
                A(PerformanceActionKind.Pose, 9.2f, 2.2f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", 4f), new PoseValue("AngleZ", 4f),
                    new PoseValue("ParamAngleZ", 3f),
                }),
                A(PerformanceActionKind.Blink, 9.6f, 0.8f, side: 1),
            },
        },
        new PerformanceDefinition
        {
            Id = "委屈抽泣",
            DisplayName = "委屈抽泣",
            Duration = 12.5f,
            Actions = new[]
            {
                A(PerformanceActionKind.Expression, 0f, 11.4f, expressions: new[] { "流泪", "＞＜" }),
                A(PerformanceActionKind.Pose, 0f, 3.6f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", 3f), new PoseValue("AngleY", 5f),
                    new PoseValue("ParamAngleY", 4f), new PoseValue("ParamMouthForm", -0.4f),
                }),
                A(PerformanceActionKind.Talk, 1.0f, 4.4f, strength: 0.55f),
                A(PerformanceActionKind.Pose, 3.8f, 3.6f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", -3f), new PoseValue("AngleY", 6f),
                    new PoseValue("ParamAngleY", 5f), new PoseValue("JawOpen", 0.3f),
                }),
                A(PerformanceActionKind.Talk, 5.6f, 4.2f, strength: 0.5f),
                A(PerformanceActionKind.Look, 8.4f, 2.6f, strength: 0.5f, direction: 4),
                A(PerformanceActionKind.Pose, 8.6f, 3.4f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", 3f), new PoseValue("AngleZ", -5f),
                    new PoseValue("ParamAngleZ", -4f), new PoseValue("PositionX", -2f),
                }),
                A(PerformanceActionKind.Blink, 10.6f, 0.9f, side: 0),
            },
        },
        new PerformanceDefinition
        {
            Id = "唱跳小曲",
            DisplayName = "唱跳小曲",
            Duration = 14.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Expression, 6.8f, 6.0f, expressions: new[] { "爱心眼" }),
                A(PerformanceActionKind.Pose, 0f, 3.4f, values: new[]
                {
                    new PoseValue("ParamBodyAngleX", 7f), new PoseValue("PositionX", 3.5f),
                    new PoseValue("AngleZ", 5f), new PoseValue("ParamAngleZ", 4f),
                    new PoseValue("HipAngleX", 4f),
                }),
                A(PerformanceActionKind.Talk, 0.4f, 3.0f, strength: 0.85f),
                A(PerformanceActionKind.Pose, 3.6f, 3.4f, values: new[]
                {
                    new PoseValue("ParamBodyAngleX", -7f), new PoseValue("PositionX", -3.5f),
                    new PoseValue("AngleZ", -5f), new PoseValue("ParamAngleZ", -4f),
                    new PoseValue("HipAngleX", -4f),
                }),
                A(PerformanceActionKind.Talk, 4.0f, 3.0f, strength: 0.85f),
                A(PerformanceActionKind.Nod, 2.0f, 2.4f, strength: 0.7f, count: 2),
                A(PerformanceActionKind.Pose, 7.2f, 3.4f, values: new[]
                {
                    new PoseValue("ParamBodyAngleX", 7f), new PoseValue("PositionX", 3.5f),
                    new PoseValue("AngleZ", 5f), new PoseValue("ParamAngleZ", 4f),
                    new PoseValue("HipAngleX", 4f),
                }),
                A(PerformanceActionKind.Talk, 7.6f, 3.0f, strength: 0.9f),
                A(PerformanceActionKind.Pose, 10.8f, 2.6f, values: new[]
                {
                    new PoseValue("ParamBodyAngleY", 8f), new PoseValue("PositionY", 3f),
                    new PoseValue("AngleY", 6f), new PoseValue("ParamAngleY", 5f),
                }),
                A(PerformanceActionKind.Nod, 11.0f, 2.4f, strength: 0.8f, count: 2),
                A(PerformanceActionKind.Smile, 12.4f, 1.4f, strength: 0.8f),
            },
        },
        new PerformanceDefinition
        {
            Id = "眼镜变装",
            DisplayName = "眼镜变装",
            Duration = 13.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Expression, 0.2f, 5.4f, expressions: new[] { "黑框眼镜" }),
                A(PerformanceActionKind.Pose, 0.4f, 4.6f, values: new[]
                {
                    new PoseValue("AngleY", 6f), new PoseValue("ParamAngleY", 5f),
                    new PoseValue("ParamBodyAngleZ", 3f), new PoseValue("ParamMouthForm", 0.4f),
                }),
                A(PerformanceActionKind.Look, 1.0f, 2.0f, strength: 0.6f, direction: 3),
                A(PerformanceActionKind.Expression, 5.6f, 4.4f, expressions: new[] { "单片眼镜" }),
                A(PerformanceActionKind.Pose, 5.8f, 4.0f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", 7f), new PoseValue("AngleZ", -5f),
                    new PoseValue("ParamAngleZ", 5f), new PoseValue("PositionX", 2.5f),
                }),
                A(PerformanceActionKind.Expression, 7.0f, 3.0f, expressions: new[] { "屑屑眼" }),
                A(PerformanceActionKind.Look, 7.4f, 2.2f, strength: 0.7f, direction: 2),
                A(PerformanceActionKind.Pose, 10.0f, 2.6f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", -6f), new PoseValue("AngleZ", 5f),
                    new PoseValue("ParamAngleZ", -4f), new PoseValue("PositionX", -2.5f),
                    new PoseValue("ParamMouthForm", -0.3f),
                }),
                A(PerformanceActionKind.Expression, 10.2f, 2.4f, expressions: new[] { "＞＜" }),
                A(PerformanceActionKind.Blink, 11.6f, 0.8f, side: 0),
            },
        },
        new PerformanceDefinition
        {
            Id = "钢盆敲敲",
            DisplayName = "钢盆敲敲",
            Duration = 12.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Expression, 0.2f, 10.6f, expressions: new[] { "钢盆" }),
                A(PerformanceActionKind.Pose, 0.4f, 4.4f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", 5f), new PoseValue("AngleZ", 7f),
                    new PoseValue("ParamAngleZ", 5f), new PoseValue("ParamMouthForm", -0.2f),
                }),
                A(PerformanceActionKind.Talk, 1.0f, 3.2f, strength: 0.5f),
                A(PerformanceActionKind.Pose, 5.0f, 4.4f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", -5f), new PoseValue("AngleZ", -7f),
                    new PoseValue("ParamAngleZ", -5f), new PoseValue("ParamEyeBallX", -0.4f),
                }),
                A(PerformanceActionKind.Expression, 5.6f, 4.8f, expressions: new[] { "＞＜" }),
                A(PerformanceActionKind.Talk, 5.8f, 3.2f, strength: 0.45f),
                A(PerformanceActionKind.Nod, 9.8f, 1.8f, strength: 0.6f, count: 1),
                A(PerformanceActionKind.Smile, 10.4f, 1.4f, strength: 0.7f),
            },
        },
        new PerformanceDefinition
        {
            Id = "变身小兔",
            DisplayName = "变身小兔",
            Duration = 14.0f,
            Actions = new[]
            {
                A(PerformanceActionKind.Expression, 0.2f, 5.6f, expressions: new[] { "体型1" }),
                A(PerformanceActionKind.Pose, 0.4f, 5.0f, values: new[]
                {
                    new PoseValue("ParamBodyAngleY", 6f), new PoseValue("PositionY", 2.5f),
                    new PoseValue("ParamAngleZ", 4f), new PoseValue("ParamMouthOpenY", 0.5f),
                }),
                A(PerformanceActionKind.Talk, 1.0f, 4.0f, strength: 0.7f),
                A(PerformanceActionKind.Nod, 3.4f, 1.8f, strength: 0.7f, count: 2),
                A(PerformanceActionKind.Expression, 6.0f, 5.4f, expressions: new[] { "体型2", "脸红" }),
                A(PerformanceActionKind.Pose, 6.2f, 5.0f, values: new[]
                {
                    new PoseValue("ParamBodyAngleX", 5f), new PoseValue("PositionX", 2.5f),
                    new PoseValue("AngleZ", 5f), new PoseValue("ParamAngleZ", 4f),
                }),
                A(PerformanceActionKind.Talk, 6.6f, 4.0f, strength: 0.6f),
                A(PerformanceActionKind.Blink, 8.6f, 0.8f, side: 1),
                A(PerformanceActionKind.Pose, 11.6f, 2.0f, values: new[]
                {
                    new PoseValue("ParamBodyAngleZ", 3f), new PoseValue("AngleZ", 4f),
                    new PoseValue("ParamAngleZ", 3f),
                }),
                A(PerformanceActionKind.Smile, 12.0f, 1.6f, strength: 0.8f),
            },
        },
    };

    private static PerformanceAction A(PerformanceActionKind kind, float at, float duration,
        float strength = 0.5f, int direction = 0, int count = 1, int side = 0, string? gestureId = null,
        PoseValue[]? values = null, string[]? expressions = null) => new()
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
        Expressions = expressions ?? Array.Empty<string>(),
    };
}
