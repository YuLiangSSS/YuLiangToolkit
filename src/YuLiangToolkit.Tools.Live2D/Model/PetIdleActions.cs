using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 桌宠待机小动作（参考 FULASKA/-live2d- 的 idle-motions / idle-faces）：
/// 不互动时每隔 8~18 秒随机做一个「活着」的小动作。内置 52 个：
/// 基础六件套（换脚 / 东张西望 / 伸懒腰 / 挥挥手 / 戴铁盆 / 爱心眼脸红）+
/// 一批表情彩蛋（圈圈眼 / ＞＜ / 屑屑眼 / 黑脸 / 加载 / 单片眼镜 / 黑框眼镜 等）+
/// 一批肢体复合编舞（鞠躬 / 踢踢腿 / 侧腰伸展 / 张开翅膀 → 已删 / 害羞扭扭 / 探头偷看 等）+
/// 三批综合编舞（封面wink吐舌 / 竖手指wink / 比心wink / 狗尾草哼歌 / 抚胸安心 / 打喷嚏 / 傲娇转身哼 等）+
/// 四批体型切换（变小卖萌 / 变小转圈 / 变小打滚 / 变小蹦迪 / 变小好奇 / 变小蹦蹦跳跳 / 变小变回来）。
/// <para>所有依赖的参数 / 表情都经过自检「彩蛋特效实拍验证」（逐像素渲染对比），
/// 实拍无效的参数（蚊香眼 / SHOCK EYE / 冒汗 / 落泪 / 炸毛 / 翅膀 / 生气符号 / 爱心弹 / 小花 / 光点 /
/// 元音口型 / 呆滞等 40+ 个）已全部移除，动作只保留真的会渲染的参数。</para>
///
/// <para><b>和现有随机手势的关系</b>：<see cref="ModelRuntime"/> 里原有的
/// 「随机手势 / 表演」（<c>IdleRandomGestureEnabled</c> / <c>IdleRandomGestureIntervalSeconds</c>）保留；
/// 这里是另一套更轻的「参数动画」调度，两套互相让位（谁在播了另一方就不抢），
/// 并且都有「互动 / 说话 / 音乐 / 动作时暂停、结束后 3 秒冷却」的规则。</para>
///
/// <para><b>写法</b>：每个动作是一条条关键帧曲线（归一化时间 0~1），每帧按
/// 「默认值 +（关键帧值 - 默认值）× 钟形包络」写参数（首尾都能接回待机呼吸，不会硬起停）；
/// 戴铁盆 / 爱心眼脸红这类会顺带施加 / 撤掉对应 Exp3 表情。
/// 参数按 id 在构造时解析成 <see cref="CubismParameter"/> 引用，运行时不查字典、不分配。</para>
/// </summary>
internal sealed class PetIdleActions
{
    private const double FadeSeconds = 0.45;
    private const double ExpressionFadeSeconds = 0.3;

    /// <summary>一条待机动作参数曲线：归一化时间的关键帧（t0,v0,t1,v1,...）。</summary>
    private sealed class IdleTrack
    {
        public CubismParameter? Parameter;
        public required float[] Keys;
    }

    private sealed class IdleRoutine
    {
        public required string Name;
        public required double Duration;
        public required IdleTrack[] Tracks;
        public string[] Expressions = Array.Empty<string>();
    }

    private readonly CubismModel _model;
    private readonly Func<string, ExpressionEntry?> _findExpression;
    private readonly Action<ExpressionEntry, double> _applyExpression;
    private readonly Action<double> _clearExpressions;
    private readonly Random _rng = new(20250302);

    private readonly IdleRoutine[] _routines;
    private readonly string[] _names;
    private readonly string[] _nonExpressionNames;

    private readonly Stack<string> _appliedExpressions = new(2);

    private int[] _touchedIndices = new int[48];
    private int _touchedCount;

    private IdleRoutine? _active;
    private double _elapsed;
    private double _timer;
    private double _cooldownRemaining;
    private bool _expressionApplied;
    private int _triggerCount;
    private float _phase;

    public PetIdleActions(
        CubismModel model,
        Func<string, ExpressionEntry?> findExpression,
        Action<ExpressionEntry, double> applyExpression,
        Action<double> clearExpressions)
    {
        _model = model;
        _findExpression = findExpression;
        _applyExpression = applyExpression;
        _clearExpressions = clearExpressions;
        _routines = BuildRoutines();
        _names = new string[_routines.Length];
        var nonExpression = new List<string>(_routines.Length);
        for (var i = 0; i < _routines.Length; i++)
        {
            _names[i] = _routines[i].Name;
            if (_routines[i].Expressions.Length == 0) nonExpression.Add(_routines[i].Name);
        }

        _nonExpressionNames = nonExpression.ToArray();
        _timer = 4.0; // 打开后先安静 4 秒再开始，避免一上来就动
    }

    /// <summary>总开关（对应配置 PetIdleActionsEnabled / 旧名的待机灵动）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否允许做「带表情」的待机小动作（对应配置 PetIdleExpressionEnabled）。</summary>
    public bool ExpressionEnabled { get; set; } = true;

    /// <summary>基础间隔（秒，默认 12；实际每次在约 8~18 秒之间随机）。</summary>
    public double IntervalSeconds { get; set; } = 12.0;

    /// <summary>能不能顺带挂表情（用户已有表情 / 额头三态时由运行时返回 false，就别挑带表情的动作）。</summary>
    public Func<bool>? CanApplyExpression { get; set; }

    /// <summary>当前是否正在播一个待机小动作。</summary>
    public bool HasActive => _active is not null;

    /// <summary>当前待机小动作名（没有为 null）。</summary>
    public string? ActiveName => _active?.Name;

    /// <summary>已触发次数（自检用）。</summary>
    public int TriggerCount => _triggerCount;

    /// <summary>互动打断后的冷却剩余（秒；到 0 才会重新开始挑动作）。</summary>
    public double CooldownRemaining => _cooldownRemaining;

    /// <summary>全部待机小动作名。</summary>
    public IReadOnlyList<string> ActionNames => _names;

    /// <summary>本帧写过的参数个数（运行时下一帧开头复位）。</summary>
    public int TouchedCount => _touchedCount;

    public int GetTouchedIndex(int index) => _touchedIndices[index];

    /// <summary>推进一帧；<paramref name="blocked"/> = 说话 / 音乐 / 动捕 / 用户动作 / 演示 / 互动中。</summary>
    public void Update(double deltaSeconds, bool blocked)
    {
        _touchedCount = 0;
        if (!Enabled)
        {
            if (_active is not null) StopImmediate();
            _timer = 0;
            _cooldownRemaining = 0;
            return;
        }

        if (_cooldownRemaining > 0)
        {
            _cooldownRemaining -= deltaSeconds;
            if (_cooldownRemaining < 0) _cooldownRemaining = 0;
        }

        // 被互动 / 说话 / 音乐 / 演示打断：立刻收掉（撤表情、复位参数），不冻结在半路
        if (blocked && _active is not null)
        {
            StopImmediate();
            if (_cooldownRemaining <= 0) _cooldownRemaining = 3.0;
        }

        if (_active is not null)
        {
            _elapsed += deltaSeconds;
            if (_elapsed >= _active.Duration)
            {
                FinishActive();
            }
            else
            {
                _phase += (float)deltaSeconds;
                WriteActive((float)(_elapsed / _active.Duration));
            }

            return;
        }

        if (blocked || _cooldownRemaining > 0)
        {
            _timer = Math.Max(_timer, 1.0); // 恢复后至少再等 1 秒，避免刚结束就蹦一个
            return;
        }

        if (_timer <= 0) _timer = NextInterval();
        _timer -= deltaSeconds;
        if (_timer > 0) return;
        if (!TryTrigger(null)) _timer = 2.0;
    }

    /// <summary>立刻播一个待机小动作（不传名字则随机；<paramref name="allowExpression"/> 用运行时最新开关）。</summary>
    public bool TryTrigger(string? name)
    {
        if (!Enabled) return false;
        if (_active is not null) StopImmediate();

        var routine = ResolveRoutine(name);
        if (routine is null) return false;

        _active = routine;
        _elapsed = 0;
        _phase = 0;
        _expressionApplied = false;
        _touchedCount = 0;
        _triggerCount++;

        if (routine.Expressions.Length > 0 && ExpressionEnabled && (CanApplyExpression?.Invoke() ?? true))
        {
            ApplyExpressions(routine.Expressions);
        }

        _timer = NextInterval();
        return true;
    }

    /// <summary>立即停止当前待机小动作并复位（参数下一帧由运行时复位）。</summary>
    public void StopImmediate()
    {
        _active = null;
        _elapsed = 0;
        _cooldownRemaining = 0;
        _touchedCount = 0;
        ClearAppliedExpressions();
    }

    /// <summary>互动开始：停掉当前动作，结束后（由 Update 判定）至少冷却 3 秒。</summary>
    public void NotifyInteraction()
    {
        StopImmediate();
        _cooldownRemaining = 3.0;
    }

    /// <summary>运行时下一帧开头复位「上一帧待机小动作写过的参数」。</summary>
    public void ResetTouched(CubismParameter[] parameters)
    {
        for (var i = 0; i < _touchedCount; i++)
        {
            var parameter = parameters[_touchedIndices[i]];
            parameter.Value = parameter.DefaultValue;
        }

        _touchedCount = 0;
    }

    private void WriteActive(float progress)
    {
        var routine = _active;
        if (routine is null) return;

        // 钟形包络：首尾各 0.45s 淡入淡出（短动作自动收窄）
        var fade = Math.Min(FadeSeconds, routine.Duration * 0.3);
        var fadeIn = fade <= 0 ? 1.0 : Math.Min(1.0, _elapsed / fade);
        var fadeOut = fade <= 0 ? 1.0 : Math.Min(1.0, (routine.Duration - _elapsed) / fade);
        var envelope = (float)DemoMath.SmoothStep((float)Math.Min(fadeIn, fadeOut));
        if (envelope <= 0) return;

        foreach (var track in routine.Tracks)
        {
            var parameter = track.Parameter;
            if (parameter is null) continue;
            var target = SampleTrack(track.Keys, progress);
            parameter.Value = DemoMath.BlendFromDefault(parameter, target, envelope);
            MarkTouched(parameter);
        }
    }

    /// <summary>动作结束时不清 touched 列表 —— 下一帧 ResetTouched 会把参数平滑归零，
    /// 不然参数会僵在上一个动作的值上（ touchedCount=0 导致没人复位）。</summary>
    private void FinishActive()
    {
        var routine = _active;
        _active = null;
        _elapsed = 0;
        if (routine is not null && routine.Expressions.Length > 0) ClearAppliedExpressions();
        // 不清 _touchedCount：下一帧 ModelRuntime.ResetTouched 会按这个列表把参数归回默认值，
        // 形成平滑过渡；如果在这里清零，参数会僵在最后的写入值（修复收回时抽搐 / 卡住）。
        _timer = NextInterval();
    }

    private void ApplyExpressions(string[] names)
    {
        _appliedExpressions.Clear();
        foreach (var name in names)
        {
            var entry = _findExpression(name);
            if (entry is null) continue;
            _applyExpression(entry, ExpressionFadeSeconds);
            _appliedExpressions.Push(name);
        }

        _expressionApplied = _appliedExpressions.Count > 0;
    }

    private void ClearAppliedExpressions()
    {
        if (!_expressionApplied) return;
        _expressionApplied = false;
        _appliedExpressions.Clear();
        _clearExpressions(ExpressionFadeSeconds);
    }

    private IdleRoutine? ResolveRoutine(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var candidates = ExpressionEnabled && (CanApplyExpression?.Invoke() ?? true) ? _names : _nonExpressionNames;
            if (candidates.Length == 0) return null;
            var selected = candidates[_rng.Next(candidates.Length)];
            foreach (var routine in _routines)
            {
                if (string.Equals(routine.Name, selected, StringComparison.Ordinal)) return routine;
            }

            return null;
        }

        var trimmed = name.Trim();
        foreach (var routine in _routines)
        {
            if (string.Equals(routine.Name, trimmed, StringComparison.Ordinal)) return routine;
        }

        return null;
    }

    /// <summary>下次间隔：基础间隔 × 0.66~1.5（默认 12s → 约 8~18s）。</summary>
    private double NextInterval()
    {
        var baseInterval = Math.Clamp(IntervalSeconds, 6.0, 60.0);
        var factor = 0.66 + (_rng.NextDouble() * 0.84);
        return baseInterval * factor;
    }

    private void MarkTouched(CubismParameter parameter)
    {
        if (_touchedCount >= _touchedIndices.Length)
        {
            Array.Resize(ref _touchedIndices, _touchedIndices.Length * 2);
        }

        _touchedIndices[_touchedCount++] = parameter.Index;
    }

    /// <summary>归一化关键帧求值（段内 smoothstep 缓动）。</summary>
    private static float SampleTrack(float[] keys, float progress)
    {
        if (keys.Length < 2) return 0;
        if (progress <= keys[0]) return keys[1];
        for (var i = 2; i + 1 < keys.Length; i += 2)
        {
            var t1 = keys[i];
            if (progress > t1) continue;
            var t0 = keys[i - 2];
            var v0 = keys[i - 1];
            var v1 = keys[i + 1];
            var span = t1 - t0;
            var k = span <= 1e-6f ? 1f : Math.Clamp((progress - t0) / span, 0f, 1f);
            return v0 + ((v1 - v0) * DemoMath.SmoothStep(k));
        }

        return keys[^1];
    }

    // ---------------- 内置待机动作数据 ----------------

    private IdleRoutine[] BuildRoutines()
    {
        var routines = new List<IdleRoutine>(8);
        routines.Add(Routine("换脚", 5.2, Array.Empty<string>(),
            Track("AngleX", 0f, 0f, 0.28f, 5.5f, 0.62f, 4.2f, 1f, 0f),
            Track("AngleZ", 0f, 0f, 0.3f, -2.6f, 0.65f, -1.8f, 1f, 0f),
            Track("ParamBodyAngleX", 0f, 0f, 0.3f, 5f, 0.65f, 3.5f, 1f, 0f),
            Track("ShoulderY", 0f, 0f, 0.3f, 2.2f, 0.7f, 1.6f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, 1.8f, 0.7f, 1.2f, 1f, 0f)));
        routines.Add(Routine("东张西望", 5.6, Array.Empty<string>(),
            Track("ParamAngleX", 0f, 0f, 0.22f, 9f, 0.46f, 8f, 0.72f, -7f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, 2.5f, 0.7f, -1.5f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.25f, -2.5f, 0.7f, 2f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.22f, 0.6f, 0.6f, -0.55f, 1f, 0f)));
        routines.Add(Routine("伸懒腰", 4.2, Array.Empty<string>(),
            Track("ParamAngleY", 0f, 0f, 0.3f, 7f, 0.55f, 8f, 0.8f, 2f, 1f, 0f),
            Track("AngleY", 0f, 0f, 0.3f, 4f, 0.6f, 4.5f, 1f, 0f),
            Track("ShoulderY", 0f, 0f, 0.35f, 3.2f, 0.7f, 2.6f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.62f, 0.7f, 0.68f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.62f, 0.7f, 0.68f, 1f, 1f),
            Track("EyeSmile", 0f, 0f, 0.3f, 0.5f, 0.7f, 0.4f, 1f, 0f)));
        routines.Add(Routine("挥挥手", 8.2, Array.Empty<string>(),
            Track("LArm", 0f, 0f, 0.14f, 0.9f, 0.3f, 0.75f, 0.5f, 0.9f, 0.7f, 0.75f, 0.9f, 0.2f, 1f, 0f),
            Track("LForearm", 0f, 0f, 0.16f, 0.5f, 0.34f, 0.8f, 0.5f, 0.5f, 0.66f, 0.8f, 0.84f, 0.3f, 1f, 0f),
            Track("LWrist", 0f, 0f, 0.2f, 0.5f, 0.36f, -0.5f, 0.52f, 0.5f, 0.68f, -0.5f, 0.86f, 0.2f, 1f, 0f),
            Track("ShoulderY", 0f, 0f, 0.2f, 2.4f, 0.7f, 1.6f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, -3f, 0.7f, 3f, 1f, 0f),
            Track("ParamAngleX", 0f, 0f, 0.3f, 4f, 0.75f, 5f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.3f, 0.15f, 0.8f, 0.2f, 1f, 0f),
            Track("MouthSmile_L", 0f, 0f, 0.2f, 0.45f, 0.8f, 0.4f, 1f, 0f),
            Track("MouthSmile_R", 0f, 0f, 0.2f, 0.45f, 0.8f, 0.4f, 1f, 0f)));
        routines.Add(Routine("戴铁盆", 5.4, new[] { "钢盆" },
            Track("ParamAngleZ", 0f, 0f, 0.12f, -7f, 0.28f, 7f, 0.44f, -6f, 0.6f, 6f, 0.76f, -4f, 0.9f, 3f, 1f, 0f),
            Track("ParamAngleX", 0f, 0f, 0.18f, 4f, 0.5f, -4f, 0.82f, 3f, 1f, 0f),
            Track("AngleZ", 0f, 0f, 0.14f, -3f, 0.34f, 3f, 0.56f, -2.5f, 0.76f, 2.2f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, -2f, 0.7f, 1.5f, 1f, 0f)));
        routines.Add(Routine("爱心眼脸红", 3.5, new[] { "爱心眼", "脸红" },
            Track("ParamAngleZ", 0f, 0f, 0.25f, 3f, 0.6f, -2f, 1f, 0f),
            Track("ParamEyeBallY", 0f, 0f, 0.25f, 0.2f, 0.7f, 0.1f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, 2f, 0.7f, 0.5f, 1f, 0f)));

        // ---------------- 2026-11 扩充（用户点名：圈圈眼 / ＞＜ / 屑屑眼 + 能想到的都加） ----------------

        // 圈圈眼发呆：圈圈眼表情（实拍生效）+ 歪头 + 眼神漂移
        routines.Add(Routine("圈圈眼发呆", 4.6, new[] { "圈圈眼" },
            Track("ParamAngleZ", 0f, 0f, 0.3f, 4.5f, 0.7f, 3f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.35f, 2.5f, 0.7f, 1.5f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.4f, -0.4f, 0.8f, 0.35f, 1f, 0f)));

        // 慌张摇头：＞＜ 表情（实拍生效）+ 快速小幅摇头 + 耸肩
        routines.Add(Routine("慌张摇头", 3.2, new[] { "＞＜" },
            Track("ParamAngleX", 0f, 0f, 0.16f, 5f, 0.3f, -5f, 0.45f, 5f, 0.6f, -5f, 0.75f, 3f, 1f, 0f),
            Track("ShoulderY", 0f, 0f, 0.25f, 2.5f, 0.7f, 1.5f, 1f, 0f)));

        // 屑屑眼无语：屑屑眼表情（实拍生效）+ 低头 + 眉毛耷拉
        routines.Add(Routine("屑屑眼无语", 4.4, new[] { "屑屑眼" },
            Track("ParamAngleY", 0f, 0f, 0.35f, 4f, 0.75f, 3f, 1f, 0f),
            Track("ParamBrowLY", 0f, 0f, 0.35f, -1.2f, 0.75f, -0.8f, 1f, 0f),
            Track("ParamBrowRY", 0f, 0f, 0.35f, -1.2f, 0.75f, -0.8f, 1f, 0f)));

        // 黑脸嫌弃：黑脸表情（实拍生效）+ 歪头 + 身体后仰 + 眼神瞟开
        routines.Add(Routine("黑脸嫌弃", 3.6, new[] { "黑脸" },
            Track("ParamAngleZ", 0f, 0f, 0.3f, -4.5f, 0.7f, -3f, 1f, 0f),
            Track("ParamBodyAngleX", 0f, 0f, 0.35f, -3f, 0.7f, -2f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.3f, -0.5f, 0.7f, -0.4f, 1f, 0f)));

        // 流冷汗尴尬：转头回避 + 耸肩 + 眉毛挑起（流冷汗表情实拍无效，纯肢体版）
        routines.Add(Routine("流冷汗尴尬", 4.4, Array.Empty<string>(),
            Track("ParamAngleX", 0f, 0f, 0.3f, 8f, 0.7f, 7f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, 3f, 0.7f, 2f, 1f, 0f),
            Track("ShoulderY", 0f, 0f, 0.3f, 3f, 0.65f, 2.2f, 1f, 0f),
            Track("ParamBrowLY", 0f, 0f, 0.3f, 1.2f, 0.7f, 0.8f, 1f, 0f),
            Track("ParamBrowRY", 0f, 0f, 0.3f, 1.2f, 0.7f, 0.8f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.35f, 0.8f, 0.35f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.35f, 0.8f, 0.35f, 1f, 1f)));

        // 假哭抽泣：流泪表情（实拍接近阈值，泪滴有出现）+ 低头抽泣两下
        routines.Add(Routine("假哭抽泣", 4.8, new[] { "流泪" },
            Track("ParamAngleY", 0f, 0f, 0.3f, 4.5f, 0.45f, 3.5f, 0.6f, 4.5f, 0.75f, 3.5f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.35f, 2f, 0.75f, -2f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.25f, 0.8f, 0.25f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.25f, 0.8f, 0.25f, 1f, 1f)));

        // 眼冒金星：屑屑眼表情（实拍生效）+ 晕乎乎摇摆
        routines.Add(Routine("眼冒金星", 4.6, new[] { "屑屑眼" },
            Track("ParamAngleZ", 0f, 0f, 0.25f, 3.5f, 0.5f, -3.5f, 0.75f, 3f, 1f, 0f),
            Track("ParamAngleX", 0f, 0f, 0.3f, 2.5f, 0.55f, -2.5f, 0.8f, 1.5f, 1f, 0f)));

        // 吓一跳：后仰 + 双眼瞪大（纯参数；SHOCKEYE / Shock 参数实拍无效已移除）
        routines.Add(Routine("吓一跳", 2.8, Array.Empty<string>(),
            Track("ParamAngleY", 0f, 0f, 0.14f, -6f, 0.5f, -4f, 1f, 0f),
            Track("ParamBodyAngleY", 0f, 0f, 0.14f, -5f, 0.55f, -3f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.12f, 1.4f, 0.85f, 1.3f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.12f, 1.4f, 0.85f, 1.3f, 1f, 1f),
            Track("ParamEyeBallY", 0f, 0f, 0.12f, -0.5f, 0.6f, -0.4f, 1f, 0f)));

        // 兔牙笑：兔牙表情 + 歪头 + 眯眼笑 + 身体轻晃
        routines.Add(Routine("兔牙笑", 3.6, new[] { "兔牙" },
            Track("ParamAngleZ", 0f, 0f, 0.3f, 4f, 0.7f, -3f, 1f, 0f),
            Track("EyeSmile", 0f, 0f, 0.3f, 0.8f, 0.8f, 0.6f, 1f, 0f),
            Track("ParamBodyAngleX", 0f, 0f, 0.3f, 2f, 0.7f, 1.5f, 1f, 0f)));

        // 单片眼镜贵气：转头 + 抬下巴 + 眉毛压低（装模作样）
        routines.Add(Routine("单片眼镜", 5.2, new[] { "单片眼镜" },
            Track("ParamAngleY", 0f, 0f, 0.3f, 7f, 0.7f, 6f, 1f, 0f),
            Track("ParamAngleX", 0f, 0f, 0.3f, -4f, 0.7f, -3f, 1f, 0f),
            Track("ParamBrowLY", 0f, 0f, 0.3f, -1f, 0.7f, -0.8f, 1f, 0f),
            Track("ParamBrowRY", 0f, 0f, 0.3f, -1f, 0.7f, -0.8f, 1f, 0f)));

        // 黑框眼镜看书：低头看书 + 半眯眼 + 缓慢点头
        routines.Add(Routine("黑框眼镜看书", 5.6, new[] { "黑框眼镜" },
            Track("ParamAngleY", 0f, 0f, 0.25f, 6f, 0.5f, 6.5f, 0.75f, 5.5f, 1f, 3f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.45f, 0.75f, 0.5f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.45f, 0.75f, 0.5f, 1f, 1f),
            Track("ParamAngleX", 0f, 0f, 0.3f, -3f, 0.75f, -2.5f, 1f, 0f)));

        // 前倾盯人：身体前倾凑近 + 眼睛看向镜头
        routines.Add(Routine("前倾盯人", 4.6, new[] { "前倾" },
            Track("ParamBodyAngleY", 0f, 0f, 0.3f, 7f, 0.75f, 6f, 1f, 0f),
            Track("PositionZ", 0f, 0f, 0.3f, 2.5f, 0.75f, 2f, 1f, 0f),
            Track("ParamEyeBallY", 0f, 0f, 0.3f, 0.25f, 0.75f, 0.15f, 1f, 0f)));

        // 加载转圈：加载表情 + 头慢慢画小圈 + 眼神跟着转
        routines.Add(Routine("加载转圈", 5.0, new[] { "加载" },
            Track("ParamAngleZ", 0f, 0f, 0.2f, 3f, 0.4f, 0f, 0.6f, -3f, 0.8f, 0f, 1f, 3f, 1f, 0f),
            Track("ParamAngleX", 0f, 0f, 0.35f, 2f, 0.5f, -2f, 0.7f, 2f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.2f, 0.5f, 0.4f, 0f, 0.6f, -0.5f, 0.8f, 0f, 1f, 0.5f, 1f, 0f)));

        // 尾巴波浪：尾巴分节依次摆出波（纯尾巴戏）
        routines.Add(Routine("尾巴波浪", 4.8, Array.Empty<string>(),
            Track("Param_Angle_Rotation84", 0f, 0f, 0.2f, 12f, 0.45f, 10f, 0.7f, -8f, 1f, 0f),
            Track("Param_Angle_Rotation85", 0f, 0f, 0.25f, 10f, 0.5f, 8f, 0.75f, -7f, 1f, 0f),
            Track("Param_Angle_Rotation86", 0f, 0f, 0.3f, 9f, 0.55f, 7f, 0.8f, -6f, 1f, 0f),
            Track("Param_Angle_Rotation87", 0f, 0f, 0.35f, 8f, 0.6f, 6f, 0.85f, -5f, 1f, 0f),
            Track("Param_Angle_Rotation88", 0f, 0f, 0.4f, 7f, 0.65f, 5f, 0.9f, -4f, 1f, 0f)));

        // 耸肩卖萌：双肩一耸 + 眯眼笑 + 歪头
        routines.Add(Routine("耸肩卖萌", 3.4, Array.Empty<string>(),
            Track("ShoulderY", 0f, 0f, 0.25f, 3.2f, 0.5f, 2.4f, 0.75f, 3f, 1f, 0f),
            Track("ShoulderYy", 0f, 0f, 0.25f, 2f, 0.55f, 1.5f, 1f, 0f),
            Track("EyeSmile", 0f, 0f, 0.25f, 0.75f, 0.8f, 0.55f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, 3f, 0.7f, -2.5f, 1f, 0f),
            Track("MouthSmile_L", 0f, 0f, 0.25f, 0.5f, 0.8f, 0.45f, 1f, 0f),
            Track("MouthSmile_R", 0f, 0f, 0.25f, 0.5f, 0.8f, 0.45f, 1f, 0f)));

        // 打哈欠：下巴张大 + 眼睛眯闭 + 头后仰 + 肩膀微抬
        routines.Add(Routine("打哈欠", 4.6, Array.Empty<string>(),
            Track("JawOpen", 0f, 0f, 0.35f, 0.85f, 0.6f, 0.9f, 0.85f, 0.2f, 1f, 0f),
            Track("ParamMouthOpenY", 0f, 0f, 0.35f, 0.8f, 0.6f, 0.85f, 0.85f, 0.2f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.4f, 0.12f, 0.7f, 0.15f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.4f, 0.12f, 0.7f, 0.15f, 1f, 1f),
            Track("ParamAngleY", 0f, 0f, 0.4f, -5f, 0.7f, -4f, 1f, 0f),
            Track("ShoulderY", 0f, 0f, 0.4f, 2.5f, 0.7f, 1.8f, 1f, 0f)));

        // 吐舌头舔嘴唇：舌头伸出 + 上下舔 + 歪头
        routines.Add(Routine("吐舌头舔嘴唇", 4.0, Array.Empty<string>(),
            Track("TongueOut", 0f, 0f, 0.2f, 1f, 0.85f, 1f, 1f, 0f),
            Track("ParamTongueUpDown", 0f, 0f, 0.3f, 0.6f, 0.5f, -0.5f, 0.7f, 0.6f, 1f, 0f),
            Track("ParamTongueWidth", 0f, 0f, 0.25f, 0.5f, 0.8f, 0.4f, 1f, 0f),
            Track("ParamMouthOpenY", 0f, 0f, 0.2f, 0.35f, 0.85f, 0.3f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, 3f, 0.75f, 2f, 1f, 0f)));

        // 摇头拒绝：快速摇头两轮 + 撅嘴
        routines.Add(Routine("摇头拒绝", 2.6, Array.Empty<string>(),
            Track("ParamAngleX", 0f, 0f, 0.18f, 7f, 0.34f, -7f, 0.5f, 7f, 0.66f, -7f, 0.82f, 3f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.2f, 0.5f, 0.4f, -0.5f, 0.6f, 0.5f, 0.8f, -0.4f, 1f, 0f),
            Track("Mouthpucker", 0f, 0f, 0.2f, 0.5f, 0.85f, 0.4f, 1f, 0f)));

        // 点头答应：点头两下 + 眯眼
        routines.Add(Routine("点头答应", 2.6, Array.Empty<string>(),
            Track("ParamAngleY", 0f, 0f, 0.2f, 6f, 0.38f, 1f, 0.56f, 6f, 0.74f, 1f, 0.9f, 3f, 1f, 0f),
            Track("EyeSmile", 0f, 0f, 0.25f, 0.6f, 0.85f, 0.5f, 1f, 0f)));

        // 哼歌摇摆：o 型嘴 + 慢摇 + 身体轻晃
        routines.Add(Routine("哼歌摇摆", 5.8, Array.Empty<string>(),
            Track("MouthFunnel", 0f, 0f, 0.15f, 0.55f, 0.85f, 0.45f, 1f, 0f),
            Track("ParamMouthOpenY", 0f, 0f, 0.2f, 0.35f, 0.45f, 0.15f, 0.7f, 0.35f, 0.9f, 0.1f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.25f, 3.5f, 0.5f, -3.5f, 0.75f, 3f, 1f, 0f),
            Track("ParamBodyAngleX", 0f, 0f, 0.3f, 2.5f, 0.55f, -2.5f, 0.8f, 1.5f, 1f, 0f)));

        // ---------------- 2026-11 二批：肢体复合动作（用户点名「左右晃一下歪脸卖萌」+ 编舞类） ----------------

        // 左右摇摆卖萌：身体 / 盆骨 / 位移左右晃两轮 → 收尾歪脸眯眼卖萌
        routines.Add(Routine("左右摇摆卖萌", 7.0, Array.Empty<string>(),
            Track("ParamBodyAngleX", 0f, 0f, 0.22f, 7f, 0.36f, -7f, 0.5f, 7f, 0.62f, -7f, 0.76f, 0f, 1f, 0f),
            Track("HipAngleX", 0f, 0f, 0.22f, 4f, 0.36f, -4f, 0.5f, 4f, 0.62f, -4f, 0.76f, 0f, 1f, 0f),
            Track("PositionX", 0f, 0f, 0.22f, 3f, 0.36f, -3f, 0.5f, 3f, 0.62f, -3f, 0.76f, 0f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.76f, 0f, 0.88f, 5f, 1f, 0f),
            Track("EyeSmile", 0f, 0f, 0.78f, 0f, 0.86f, 0.8f, 0.95f, 0.8f, 1f, 0f),
            Track("MouthSmile_L", 0f, 0f, 0.78f, 0f, 0.86f, 0.55f, 0.95f, 0.55f, 1f, 0f),
            Track("MouthSmile_R", 0f, 0f, 0.78f, 0f, 0.86f, 0.55f, 0.95f, 0.55f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.76f, 0f, 0.86f, 0.3f, 1f, 0f)));

        // 打瞌睡猛醒：头慢慢垂下眼闭上 → SHOCK EYE 猛然惊醒 → 再垂一次 → 回正
        routines.Add(Routine("打瞌睡猛醒", 6.8, Array.Empty<string>(),
            Track("ParamAngleY", 0f, 0f, 0.3f, 6f, 0.5f, 7f, 0.62f, 7f, 0.68f, 2f, 0.74f, -3f, 0.8f, 6f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.5f, 0.5f, 0.15f, 0.62f, 0.15f, 0.68f, 1.3f, 0.72f, 0.3f, 0.9f, 0.3f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.5f, 0.5f, 0.15f, 0.62f, 0.15f, 0.68f, 1.3f, 0.72f, 0.3f, 0.9f, 0.3f, 1f, 1f),
            Track("Param11", 0f, 0f, 0.45f, 0.6f, 0.62f, 0.7f, 0.7f, 0f, 1f, 0f),
            Track("SHOCKEYE", 0f, 0f, 0.66f, 0.8f, 0.74f, 0f, 1f, 0f),
            Track("ShoulderY", 0f, 0f, 0.4f, 2f, 0.62f, 2f, 0.7f, 0f, 1f, 0f)));

        // 拍拍手：双臂抬起小臂拍三下 + 爱心眼
        routines.Add(Routine("拍拍手", 5.0, new[] { "爱心眼" },
            Track("LArm", 0f, 0f, 0.2f, 0.55f, 0.85f, 0.55f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.2f, 0.55f, 0.85f, 0.55f, 1f, 0f),
            Track("LForearm", 0f, 0f, 0.25f, 1f, 0.85f, 1f, 1f, 0f),
            Track("RForearm", 0f, 0f, 0.25f, 1f, 0.85f, 1f, 1f, 0f),
            Track("LWrist", 0f, 0f, 0.3f, -0.3f, 0.4f, 0.3f, 0.5f, -0.3f, 0.6f, 0.3f, 0.7f, -0.3f, 0.8f, 0f, 1f, 0f),
            Track("RWrist", 0f, 0f, 0.3f, 0.3f, 0.4f, -0.3f, 0.5f, 0.3f, 0.6f, -0.3f, 0.7f, 0.3f, 0.8f, 0f, 1f, 0f)));

        // 扭腰小舞步：盆骨 + 胸腔反向扭四拍，手臂跟着摆
        routines.Add(Routine("扭腰小舞步", 6.5, Array.Empty<string>(),
            Track("HipAngleZ", 0f, 0f, 0.2f, 9f, 0.35f, -9f, 0.5f, 9f, 0.65f, -9f, 0.8f, 9f, 1f, 0f),
            Track("ParamBodyAngleZ", 0f, 0f, 0.2f, 5f, 0.35f, -5f, 0.5f, 5f, 0.65f, -5f, 0.8f, 5f, 1f, 0f),
            Track("LArm", 0f, 0f, 0.2f, 0.5f, 0.35f, -0.5f, 0.5f, 0.5f, 0.65f, -0.5f, 0.8f, 0.3f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.2f, -0.5f, 0.35f, 0.5f, 0.5f, -0.5f, 0.65f, 0.5f, 0.8f, -0.3f, 1f, 0f),
            Track("PositionX", 0f, 0f, 0.2f, 2.5f, 0.35f, -2.5f, 0.5f, 2.5f, 0.65f, -2.5f, 0.8f, 2f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.2f, 3f, 0.35f, -3f, 0.5f, 3f, 0.65f, -3f, 0.8f, 2f, 1f, 0f)));

        // 叉腰挺胸：双手叉腰 + 抬头 + 眯眼（装模作样站桩）
        routines.Add(Routine("叉腰挺胸", 5.5, Array.Empty<string>(),
            Track("LArm", 0f, 0f, 0.25f, 0.55f, 0.8f, 0.55f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.25f, 0.55f, 0.8f, 0.55f, 1f, 0f),
            Track("LForearm", 0f, 0f, 0.3f, -0.95f, 0.8f, -0.95f, 1f, 0f),
            Track("RForearm", 0f, 0f, 0.3f, -0.95f, 0.8f, -0.95f, 1f, 0f),
            Track("LWrist", 0f, 0f, 0.3f, -0.2f, 0.8f, -0.2f, 1f, 0f),
            Track("RWrist", 0f, 0f, 0.3f, 0.2f, 0.8f, 0.2f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, -4f, 0.8f, -3f, 1f, 0f),
            Track("EyeSmile", 0f, 0f, 0.3f, 0.35f, 0.8f, 0.3f, 1f, 0f)));

        // 揉眼睛困困：双手抬到脸 + 眼睛闭上揉三下 + 低头呆滞
        routines.Add(Routine("揉眼睛困困", 5.5, Array.Empty<string>(),
            Track("LArm", 0f, 0f, 0.25f, 0.95f, 0.75f, 0.9f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.25f, 0.95f, 0.75f, 0.9f, 1f, 0f),
            Track("LForearm", 0f, 0f, 0.3f, 0.4f, 0.7f, 0.4f, 1f, 0f),
            Track("RForearm", 0f, 0f, 0.3f, 0.4f, 0.7f, 0.4f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.35f, 0.1f, 0.45f, 0.5f, 0.55f, 0.1f, 0.65f, 0.5f, 0.75f, 0.1f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.35f, 0.1f, 0.45f, 0.5f, 0.55f, 0.1f, 0.65f, 0.5f, 0.75f, 0.1f, 1f, 1f),
            Track("ParamAngleY", 0f, 0f, 0.3f, 5f, 0.8f, 4f, 1f, 0f),
            Track("Param11", 0f, 0f, 0.6f, 0.5f, 1f, 0f)));

        // 鞠躬：胸腔前倾 + 低头 + 双手身前 + 起身
        routines.Add(Routine("鞠躬", 4.5, Array.Empty<string>(),
            Track("ParamBodyAngleY", 0f, 0f, 0.3f, 15f, 0.55f, 16f, 0.8f, 1f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, 10f, 0.55f, 11f, 0.8f, 1f, 1f, 0f),
            Track("LArm", 0f, 0f, 0.3f, 0.35f, 0.55f, 0.4f, 0.8f, 0.05f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.3f, 0.35f, 0.55f, 0.4f, 0.8f, 0.05f, 1f, 0f),
            Track("LForearm", 0f, 0f, 0.3f, 0.5f, 0.55f, 0.55f, 0.8f, 0f, 1f, 0f),
            Track("RForearm", 0f, 0f, 0.3f, 0.5f, 0.55f, 0.55f, 0.8f, 0f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.4f, 0.55f, 0.45f, 0.85f, 1f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.4f, 0.55f, 0.45f, 0.85f, 1f, 1f, 1f),
            Track("PositionZ", 0f, 0f, 0.3f, 2f, 0.55f, 2.2f, 0.85f, 0f, 1f, 0f)));

        // 踢踢腿：双腿交替摆 + 身体反向平衡 + 手臂摆
        routines.Add(Routine("踢踢腿", 4.0, Array.Empty<string>(),
            Track("LEGAngleZ2", 0f, 0f, 0.2f, 9f, 0.35f, 0f, 0.5f, -9f, 0.65f, 0f, 0.8f, 4f, 1f, 0f),
            Track("HipAngleX", 0f, 0f, 0.2f, 3f, 0.35f, 0f, 0.5f, 3f, 0.65f, 0f, 1f, 0f),
            Track("ParamBodyAngleZ", 0f, 0f, 0.2f, -4f, 0.35f, 0f, 0.5f, 4f, 0.65f, 0f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.2f, 3f, 0.5f, -3f, 1f, 0f),
            Track("LArm", 0f, 0f, 0.2f, -0.4f, 0.5f, 0.4f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.2f, 0.4f, 0.5f, -0.4f, 1f, 0f)));

        // 侧腰伸展：身体侧弯 + 对侧手臂上举，左右各一次（拉伸操）
        routines.Add(Routine("侧腰伸展", 6.0, Array.Empty<string>(),
            Track("ParamBodyAngleZ", 0f, 0f, 0.25f, 10f, 0.42f, 11f, 0.55f, 0f, 0.72f, -10f, 0.85f, -11f, 1f, 0f),
            Track("LArm", 0f, 0f, 0.22f, 1f, 0.45f, 1f, 0.55f, 0f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.6f, 0f, 0.68f, 1f, 0.85f, 1f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.22f, 3f, 0.45f, 4f, 0.55f, 0f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.25f, 0.5f, 0.5f, 0.5f, 0.72f, 0.5f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.25f, 0.5f, 0.5f, 0.5f, 0.72f, 0.5f, 1f, 1f),
            Track("ShoulderY", 0f, 0f, 0.25f, 2.5f, 0.85f, 2.5f, 1f, 0f)));

        // 害羞扭扭：脸红 + 双手胸前小幅度左右扭 + 眼神躲
        routines.Add(Routine("害羞扭扭", 4.2, new[] { "脸红" },
            Track("ParamBodyAngleX", 0f, 0f, 0.25f, 2.5f, 0.4f, -2.5f, 0.55f, 2.5f, 0.7f, -2f, 1f, 0f),
            Track("LArm", 0f, 0f, 0.2f, 0.7f, 0.8f, 0.65f, 1f, 0f),
            Track("LForearm", 0f, 0f, 0.25f, 0.9f, 0.8f, 0.85f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.2f, 0.7f, 0.8f, 0.65f, 1f, 0f),
            Track("RForearm", 0f, 0f, 0.25f, 0.9f, 0.8f, 0.85f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.3f, -0.55f, 0.75f, -0.5f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, 3.5f, 0.75f, 3f, 1f, 0f)));

        // 踮脚张望：踮起脚尖 + 头 / 眼珠左右扫视
        routines.Add(Routine("踮脚张望", 5.5, Array.Empty<string>(),
            Track("PositionY", 0f, 0f, 0.2f, 3f, 0.8f, 3f, 1f, 0f),
            Track("HipAngleYUP", 0f, 0f, 0.2f, 2f, 0.8f, 2f, 1f, 0f),
            Track("ParamAngleX", 0f, 0f, 0.25f, 9f, 0.45f, -9f, 0.65f, 9f, 0.85f, -7f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.25f, 0.6f, 0.45f, -0.6f, 0.65f, 0.6f, 0.85f, -0.5f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, 2.5f, 0.6f, -2.5f, 1f, 0f)));

        // 抖肩膀：肩膀快速左右抖 + 眯眼笑
        routines.Add(Routine("抖肩膀", 3.0, Array.Empty<string>(),
            Track("ShoulderX", 0f, 0f, 0.1f, 1.5f, 0.18f, -1.5f, 0.26f, 1.5f, 0.34f, -1.5f, 0.42f, 1.5f, 0.5f, -1.5f, 0.58f, 1.5f, 0.66f, -1.5f, 0.74f, 1f, 0.82f, -0.8f, 0.9f, 0f, 1f, 0f),
            Track("ShoulderY", 0f, 0f, 0.2f, 1.5f, 0.7f, 1.5f, 1f, 0f),
            Track("MouthSmile_L", 0f, 0f, 0.15f, 0.5f, 0.85f, 0.45f, 1f, 0f),
            Track("MouthSmile_R", 0f, 0f, 0.15f, 0.5f, 0.85f, 0.45f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.15f, 0.55f, 0.85f, 0.55f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.15f, 0.55f, 0.85f, 0.55f, 1f, 1f)));

        // 手搭凉棚：左手搭额头 + 远眺（眼珠上翻）+ 抬头
        routines.Add(Routine("手搭凉棚", 5.5, Array.Empty<string>(),
            Track("LArm", 0f, 0f, 0.22f, 1f, 0.8f, 1f, 1f, 0f),
            Track("LForearm", 0f, 0f, 0.3f, 0.85f, 0.8f, 0.85f, 1f, 0f),
            Track("LWrist", 0f, 0f, 0.35f, -0.85f, 0.8f, -0.85f, 1f, 0f),
            Track("ParamEyeBallY", 0f, 0f, 0.35f, 1.8f, 0.8f, 1.7f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, -4f, 0.8f, -3.5f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.75f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.75f, 1f, 1f)));

        // 生气跺脚：皱眉撇嘴 + 跺脚三下 + 身体抖（Angry 生气符号实拍无效已移除）
        routines.Add(Routine("生气跺脚", 4.0, Array.Empty<string>(),
            Track("ParamBrowRAngle", 0f, 0f, 0.15f, -0.6f, 0.85f, -0.5f, 1f, 0f),
            Track("ParamBrowLAngle", 0f, 0f, 0.15f, -0.6f, 0.85f, -0.5f, 1f, 0f),
            Track("ParamMouthForm", 0f, 0f, 0.15f, -0.6f, 0.85f, -0.5f, 1f, 0f),
            Track("PositionY", 0f, 0f, 0.2f, 2f, 0.28f, 0f, 0.36f, 2f, 0.44f, 0f, 0.52f, 2f, 0.6f, 0f, 1f, 0f),
            Track("LEGAngleZ2", 0f, 0f, 0.2f, 3f, 0.28f, -3f, 0.36f, 3f, 0.44f, -3f, 0.52f, 3f, 0.6f, -3f, 1f, 0f),
            Track("ParamBodyAngleZ", 0f, 0f, 0.2f, 2f, 0.3f, -2f, 0.4f, 2f, 0.5f, -2f, 1f, 0f)));

        // ---------------- 2026-11 三批：表情 + 头 + 肢体综合编舞（全部用实拍验证过的参数） ----------------

        // 封面 wink 吐舌：歪头 + 微微抬头 + 单眼闭 + 闭嘴吐舌 + 身体扭向一边（还原官方封面 pose）
        routines.Add(Routine("封面wink吐舌", 5.0, Array.Empty<string>(),
            Track("ParamAngleZ", 0f, 0f, 0.3f, 8f, 0.85f, 7f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, 4f, 0.85f, 3f, 1f, 0f),
            Track("ParamBodyAngleZ", 0f, 0f, 0.3f, 5f, 0.85f, 4f, 1f, 0f),
            Track("ParamBodyAngleX", 0f, 0f, 0.3f, 2f, 0.85f, 1.5f, 1f, 0f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.05f, 0.85f, 0.05f, 1f, 1f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 1.05f, 0.85f, 1.05f, 1f, 1f),
            Track("TongueOut", 0f, 0f, 0.35f, 1f, 0.85f, 1f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.3f, 0.25f, 0.85f, 0.2f, 1f, 0f)));

        // 竖手指 wink：竖起食指（Movement3 实拍生效）+ 身体晃一下 + 单眼眨一下
        routines.Add(Routine("竖手指wink", 5.5, Array.Empty<string>(),
            Track("Movement3", 0f, 0f, 0.15f, 1f, 0.9f, 1f, 1f, 0f),
            Track("ParamBodyAngleX", 0f, 0f, 0.3f, 3f, 0.5f, -3f, 0.7f, 2f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.25f, 4f, 0.75f, 3f, 1f, 0f),
            Track("ParamEyeROpen", 1f, 1f, 0.42f, 1f, 0.46f, 0.08f, 0.52f, 1f, 0.82f, 0.08f, 0.88f, 1f, 1f, 1f),
            Track("ParamEyeBallX", 0f, 0f, 0.3f, 0.3f, 1f, 0f),
            Track("MouthSmile_L", 0f, 0f, 0.25f, 0.45f, 0.85f, 0.4f, 1f, 0f),
            Track("MouthSmile_R", 0f, 0f, 0.25f, 0.45f, 0.85f, 0.4f, 1f, 0f)));

        // 比心 wink：双手比心（LHand / RHand 实拍生效）+ 脸红表情 + 单眼 wink + 歪头
        routines.Add(Routine("比心wink", 4.8, new[] { "脸红" },
            Track("LArm", 0f, 0f, 0.25f, 0.45f, 0.8f, 0.45f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.25f, 0.45f, 0.8f, 0.45f, 1f, 0f),
            Track("LForearm", 0f, 0f, 0.3f, 1f, 0.8f, 1f, 1f, 0f),
            Track("RForearm", 0f, 0f, 0.3f, 1f, 0.8f, 1f, 1f, 0f),
            Track("LWrist", 0f, 0f, 0.35f, -0.8f, 0.8f, -0.8f, 1f, 0f),
            Track("RWrist", 0f, 0f, 0.35f, 0.8f, 0.8f, 0.8f, 1f, 0f),
            Track("LHand", 0f, 0f, 0.35f, 0.5f, 0.8f, 0.5f, 1f, 0f),
            Track("RHand", 0f, 0f, 0.35f, 0.5f, 0.8f, 0.5f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, -4f, 0.8f, -3f, 1f, 0f),
            Track("ParamEyeROpen", 1f, 1f, 0.4f, 0.15f, 0.8f, 0.15f, 1f, 1f),
            Track("ParamEyeLOpen", 1f, 1f, 0.4f, 0.8f, 0.8f, 0.8f, 1f, 1f),
            Track("ParamAngleY", 0f, 0f, 0.3f, 2f, 1f, 0f)));

        // 狗尾草哼歌：叼狗尾草（Movement1 实拍生效）+ 摇头晃脑 + 哼哼开合嘴
        routines.Add(Routine("狗尾草哼歌", 6.0, Array.Empty<string>(),
            Track("Movement1", 0f, 0f, 0.12f, 1f, 0.92f, 1f, 1f, 0f),
            Track("ParamAngleX", 0f, 0f, 0.25f, 5f, 0.45f, -5f, 0.65f, 4f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, 3f, 0.55f, -3f, 1f, 0f),
            Track("ParamMouthOpenY", 0f, 0f, 0.2f, 0.35f, 0.35f, 0.15f, 0.5f, 0.35f, 0.65f, 0.15f, 0.8f, 0.3f, 1f, 0f),
            Track("ParamBodyAngleX", 0f, 0f, 0.3f, 2.5f, 0.6f, -2.5f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.25f, 0.65f, 0.85f, 0.65f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.25f, 0.65f, 0.85f, 0.65f, 1f, 1f)));

        // 抚胸安心：手放胸口（Movement2 实拍生效）+ 闭眼深呼吸起伏 + 放心笑
        routines.Add(Routine("抚胸安心", 4.6, Array.Empty<string>(),
            Track("Movement2", 0f, 0f, 0.15f, 1f, 0.9f, 1f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, -4f, 0.75f, -3f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.35f, 0.3f, 0.7f, 0.35f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.35f, 0.3f, 0.7f, 0.35f, 1f, 1f),
            Track("ParamBodyAngleY", 0f, 0f, 0.3f, 2.5f, 0.5f, 3.5f, 0.75f, 2f, 1f, 0f),
            Track("EyeSmile", 0f, 0f, 0.5f, 0.4f, 0.85f, 0.35f, 1f, 0f)));

        // 打喷嚏：仰头蓄力 → 前冲闭眼张嘴打出来 → 回正
        routines.Add(Routine("打喷嚏", 3.4, Array.Empty<string>(),
            Track("ParamAngleY", 0f, 0f, 0.25f, -8f, 0.35f, -8f, 0.42f, 10f, 0.5f, 10f, 0.6f, -2f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.6f, 0.42f, 0.05f, 0.55f, 0.1f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.6f, 0.42f, 0.05f, 0.55f, 0.1f, 1f, 1f),
            Track("JawOpen", 0f, 0f, 0.42f, 0.95f, 0.55f, 0.4f, 1f, 0f),
            Track("ParamMouthOpenY", 0f, 0f, 0.42f, 0.9f, 0.55f, 0.5f, 1f, 0f),
            Track("PositionZ", 0f, 0f, 0.42f, 2f, 0.55f, 1f, 1f, 0f)));

        // 探头偷看：从边上探进来 + 瞪大眼扫一眼 + 缩回去
        routines.Add(Routine("探头偷看", 4.6, Array.Empty<string>(),
            Track("PositionX", 0f, 0f, 0.25f, 7f, 0.7f, 7f, 0.85f, 3f, 1f, 0f),
            Track("ParamAngleX", 0f, 0f, 0.25f, 10f, 0.7f, 9f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.3f, 0.6f, 0.8f, 0.55f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 1.25f, 0.8f, 1.2f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 1.25f, 0.8f, 1.2f, 1f, 1f),
            Track("ParamBodyAngleZ", 0f, 0f, 0.25f, 3f, 0.8f, 2.5f, 1f, 0f)));

        // 傲娇转身哼：扭头转身 + 闭眼撇嘴 → 结尾偷偷瞄一眼
        routines.Add(Routine("傲娇转身哼", 5.2, Array.Empty<string>(),
            Track("ParamBodyAngleZ", 0f, 0f, 0.25f, -10f, 0.75f, -9f, 1f, 0f),
            Track("PositionX", 0f, 0f, 0.25f, -4f, 0.75f, -3.5f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.12f, 0.72f, 0.12f, 0.85f, 0.5f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.12f, 0.72f, 0.12f, 0.85f, 0.5f, 1f, 1f),
            Track("Mouthpucker", 0f, 0f, 0.3f, 0.7f, 0.8f, 0.6f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, -3f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.82f, 0.5f, 0.92f, 0.55f, 1f, 0f)));

        // 鼓腮帮赌气：左右腮帮轮流鼓（CheeckPuff 系实拍生效）+ 撅嘴闭眼转头
        routines.Add(Routine("鼓腮帮赌气", 4.4, Array.Empty<string>(),
            Track("CheeckPuff", 0f, 0f, 0.25f, 1f, 0.8f, 1f, 1f, 0f),
            Track("CheeckPuffR", 0f, 0f, 0.3f, 0.7f, 0.5f, 0.2f, 0.65f, 0.7f, 0.85f, 0.5f, 1f, 0f),
            Track("CheeckPuffL", 0f, 0f, 0.3f, 0.2f, 0.5f, 0.7f, 0.65f, 0.2f, 0.85f, 0.5f, 1f, 0f),
            Track("Mouthpucker", 0f, 0f, 0.25f, 0.7f, 0.85f, 0.6f, 1f, 0f),
            Track("ParamAngleX", 0f, 0f, 0.25f, 7f, 0.8f, 6f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.2f, 0.85f, 0.2f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.2f, 0.85f, 0.2f, 1f, 1f)));

        // 看手腕唉：抬手看手腕 + 头偏过去 + 皱眉（唉——好慢）
        routines.Add(Routine("看手腕唉", 6.0, Array.Empty<string>(),
            Track("RArm", 0f, 0f, 0.25f, 0.95f, 0.8f, 0.95f, 1f, 0f),
            Track("RForearm", 0f, 0f, 0.3f, 0.75f, 0.8f, 0.75f, 1f, 0f),
            Track("RWrist", 0f, 0f, 0.35f, 0.6f, 0.8f, 0.6f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, -6f, 0.8f, -5.5f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, 4f, 0.8f, 3.5f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.32f, 0.65f, 0.8f, 0.6f, 1f, 0f),
            Track("ParamBrowLY", 0f, 0f, 0.45f, -0.8f, 0.8f, -0.7f, 1f, 0f),
            Track("ParamBrowRY", 0f, 0f, 0.45f, -0.8f, 0.8f, -0.7f, 1f, 0f)));

        // 爱心蹦蹦：爱心眼表情（实拍生效）+ 原地蹦两下
        routines.Add(Routine("爱心蹦蹦", 4.6, new[] { "爱心眼" },
            Track("PositionY", 0f, 0f, 0.22f, 2f, 0.4f, 0f, 0.58f, 2f, 0.75f, 0f, 1f, 0f),
            Track("ParamBodyAngleX", 0f, 0f, 0.3f, 2.5f, 0.6f, -2.5f, 1f, 0f),
            Track("ParamEyeBallY", 0f, 0f, 0.3f, 0.25f, 0.7f, 0.15f, 1f, 0f)));

        // 开心转圈：双手展开 + 身体左右转一圈（原撒花特效实拍无效已改名）
        routines.Add(Routine("开心转圈", 6.0, Array.Empty<string>(),
            Track("ParamBodyAngleZ", 0f, 0f, 0.3f, 8f, 0.5f, -8f, 0.7f, 8f, 1f, 0f),
            Track("LArm", 0f, 0f, 0.25f, 1f, 0.8f, 1f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.25f, 1f, 0.8f, 1f, 1f, 0f),
            Track("LForearm", 0f, 0f, 0.25f, 0.7f, 0.8f, 0.7f, 1f, 0f),
            Track("RForearm", 0f, 0f, 0.25f, 0.7f, 0.8f, 0.7f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, -3f, 0.8f, -2f, 1f, 0f),
            Track("EyeSmile", 0f, 0f, 0.25f, 0.8f, 0.8f, 0.7f, 1f, 0f)));

        // ---------------- 2026-11 四批：体型切换（变小做动作再变回来） ----------------
        // 体型1 / 体型2 表情实拍生效（71421 / 70935 像素变化 = 整个模型重绘级别），
        // Clothes2（大小变）参数也可以直接用关键帧做平滑过渡。
        // 体型切换是长动作（7~10 秒），框架在结束时自动清除表情 → 变回默认体型。

        // 变小卖萌：体型1 缩小 + 脸红 + 蹦跳 + 歪头卖萌
        routines.Add(Routine("变小卖萌", 8.5, new[] { "体型1", "脸红" },
            Track("PositionY", 0f, 0f, 0.2f, 2.5f, 0.32f, 0f, 0.44f, 2.5f, 0.56f, 0f, 0.68f, 2.5f, 0.8f, 0f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.5f, 4f, 0.65f, -3f, 0.8f, 3.5f, 1f, 0f),
            Track("EyeSmile", 0f, 0f, 0.35f, 0.7f, 0.7f, 0.6f, 1f, 0f),
            Track("MouthSmile_L", 0f, 0f, 0.35f, 0.5f, 0.7f, 0.45f, 1f, 0f),
            Track("MouthSmile_R", 0f, 0f, 0.35f, 0.5f, 0.7f, 0.45f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.4f, 0.3f, 0.7f, -0.3f, 1f, 0f)));

        // 变小转圈：体型2 缩小 + 双手展开转一圈
        routines.Add(Routine("变小转圈", 9.0, new[] { "体型2" },
            Track("ParamBodyAngleZ", 0f, 0f, 0.3f, 8f, 0.5f, -8f, 0.7f, 8f, 1f, 0f),
            Track("LArm", 0f, 0f, 0.25f, 1f, 0.8f, 1f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.25f, 1f, 0.8f, 1f, 1f, 0f),
            Track("LForearm", 0f, 0f, 0.25f, 0.7f, 0.8f, 0.7f, 1f, 0f),
            Track("RForearm", 0f, 0f, 0.25f, 0.7f, 0.8f, 0.7f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, -3f, 0.8f, -2f, 1f, 0f),
            Track("EyeSmile", 0f, 0f, 0.25f, 0.8f, 0.8f, 0.7f, 1f, 0f)));

        // 变小打滚：体型1 缩小 + 身体左右打滚 + 腿踢
        routines.Add(Routine("变小打滚", 7.5, new[] { "体型1" },
            Track("ParamBodyAngleZ", 0f, 0f, 0.2f, 12f, 0.4f, -12f, 0.6f, 10f, 0.8f, -8f, 1f, 0f),
            Track("LEGAngleZ2", 0f, 0f, 0.25f, 8f, 0.45f, -8f, 0.65f, 6f, 1f, 0f),
            Track("LArm", 0f, 0f, 0.2f, 0.8f, 0.5f, -0.5f, 0.8f, 0.6f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.2f, -0.6f, 0.45f, 0.7f, 0.75f, -0.5f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.3f, 0.7f, 0.35f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.3f, 0.7f, 0.35f, 1f, 1f)));

        // 变小蹦迪：体型2 缩小 + 多拍舞步（蹦 + 摆手 + 点头）
        routines.Add(Routine("变小蹦迪", 9.5, new[] { "体型2" },
            Track("PositionY", 0f, 0f, 0.15f, 2f, 0.25f, 0f, 0.35f, 2f, 0.45f, 0f, 0.55f, 2f, 0.65f, 0f, 0.75f, 2f, 0.85f, 0f, 1f, 0f),
            Track("LArm", 0f, 0f, 0.2f, 0.8f, 0.3f, 0.3f, 0.4f, 0.8f, 0.5f, 0.3f, 0.6f, 0.8f, 0.7f, 0.3f, 0.8f, 0.5f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.2f, 0.3f, 0.3f, 0.8f, 0.4f, 0.3f, 0.5f, 0.8f, 0.6f, 0.3f, 0.7f, 0.8f, 0.8f, 0.5f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.25f, 4f, 0.45f, -4f, 0.65f, 4f, 0.85f, -3f, 1f, 0f),
            Track("ParamMouthOpenY", 0f, 0f, 0.2f, 0.4f, 0.3f, 0.15f, 0.4f, 0.4f, 0.5f, 0.15f, 0.6f, 0.4f, 0.7f, 0.15f, 0.8f, 0.4f, 1f, 0f)));

        // 变小好奇：体型1 缩小 + 歪头四处张望 + 眼睛瞪大
        routines.Add(Routine("变小好奇", 7.0, new[] { "体型1" },
            Track("ParamAngleX", 0f, 0f, 0.25f, 8f, 0.45f, -8f, 0.65f, 7f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, 4f, 0.55f, -3f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.25f, 0.55f, 0.45f, -0.55f, 0.65f, 0.5f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.25f, 1.2f, 0.85f, 1.15f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.25f, 1.2f, 0.85f, 1.15f, 1f, 1f),
            Track("LArm", 0f, 0f, 0.3f, 0.4f, 0.7f, 0.35f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.3f, 0.4f, 0.7f, 0.35f, 1f, 0f)));

        // 变小蹦蹦跳跳：体型2 缩小 + 连续蹦跳 + 左右移动 + 手臂摆
        routines.Add(Routine("变小蹦蹦跳跳", 8.0, new[] { "体型2" },
            Track("PositionY", 0f, 0f, 0.12f, 2.5f, 0.22f, 0f, 0.32f, 2.5f, 0.42f, 0f, 0.52f, 2.5f, 0.62f, 0f, 0.72f, 2.5f, 0.82f, 0f, 1f, 0f),
            Track("PositionX", 0f, 0f, 0.22f, 3f, 0.42f, -3f, 0.62f, 3f, 0.82f, -2f, 1f, 0f),
            Track("LArm", 0f, 0f, 0.15f, 0.9f, 0.25f, 0.2f, 0.35f, 0.9f, 0.45f, 0.2f, 0.55f, 0.9f, 0.65f, 0.2f, 0.75f, 0.8f, 0.85f, 0.3f, 1f, 0f),
            Track("RArm", 0f, 0f, 0.15f, 0.2f, 0.25f, 0.9f, 0.35f, 0.2f, 0.45f, 0.9f, 0.55f, 0.2f, 0.65f, 0.9f, 0.75f, 0.3f, 0.85f, 0.6f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.22f, 3f, 0.42f, -3f, 0.62f, 3f, 0.82f, -2f, 1f, 0f)));

        // 变小变回来：Clothes2 参数平滑缩小 → 蹦一下 → 平滑变回（不用表情，纯参数过渡）
        routines.Add(Routine("变小变回来", 8.0, Array.Empty<string>(),
            Track("Clothes2", 0f, 0f, 0.15f, 1f, 0.8f, 1f, 1f, 0f),
            Track("Param34", 0f, 0f, 0.15f, 1f, 0.8f, 1f, 1f, 0f),
            Track("PositionY", 0f, 0f, 0.35f, 2.5f, 0.45f, 0f, 0.55f, 2.5f, 0.65f, 0f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.4f, 3f, 0.55f, -2.5f, 0.7f, 3f, 1f, 0f),
            Track("EyeSmile", 0f, 0f, 0.3f, 0.7f, 0.75f, 0.6f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.3f, 0.75f, 0.8f, 0.75f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.3f, 0.75f, 0.8f, 0.75f, 1f, 1f)));

        return routines.ToArray();
    }

    private IdleTrack Track(string parameterId, params float[] keys)
    {
        var parameter = _model.FindParameter(parameterId);
        return new IdleTrack { Parameter = parameter, Keys = keys };
    }

    private static IdleRoutine Routine(string name, double duration, string[] expressions, params IdleTrack[] tracks)
        => new() { Name = name, Duration = duration, Tracks = tracks, Expressions = expressions };
}
