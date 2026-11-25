using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 桌宠待机小动作（参考 FULASKA/-live2d- 的 idle-motions / idle-faces）：
/// 不互动时每隔 8~18 秒随机做一个「活着」的小动作。内置 26 个：
/// 换脚、东张西望、伸懒腰、挥挥手、戴铁盆、爱心眼脸红（基础六个）+
/// 圈圈眼发呆、慌张摇头（＞＜）、屑屑眼无语、黑脸嫌弃、流冷汗尴尬、假哭流泪、眼冒金星、
/// 吓一跳、兔牙笑、单片眼镜、黑框眼镜看书、前倾盯人、加载转圈、尾巴波浪、耸肩卖萌、
/// 打哈欠、吐舌头舔嘴唇、摇头拒绝、点头答应、哼歌摇摆（2026-11 扩充）。
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

    private void FinishActive()
    {
        var routine = _active;
        _active = null;
        _elapsed = 0;
        if (routine is not null && routine.Expressions.Length > 0) ClearAppliedExpressions();
        _touchedCount = 0;
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
            Track("PositionX", 0f, 0f, 0.3f, 2.6f, 0.65f, 2.0f, 1f, 0f),
            Track("ShoulderY", 0f, 0f, 0.3f, 2.2f, 0.7f, 1.6f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, 1.8f, 0.7f, 1.2f, 1f, 0f)));
        routines.Add(Routine("东张西望", 5.6, Array.Empty<string>(),
            Track("ParamAngleX", 0f, 0f, 0.22f, 9f, 0.46f, 8f, 0.72f, -7f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, 2.5f, 0.7f, -1.5f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.25f, -2.5f, 0.7f, 2f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.22f, 0.6f, 0.6f, -0.55f, 1f, 0f),
            Track("PositionX", 0f, 0f, 0.3f, 1.6f, 0.75f, -1.3f, 1f, 0f)));
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

        // 圈圈眼发呆：蚊香眼参数直接转 + 圈圈眼表情 + 歪头眼神漂移
        routines.Add(Routine("圈圈眼发呆", 4.6, new[] { "圈圈眼" },
            Track("AniEyerolling", 0f, 0f, 0.3f, 1f, 0.85f, 1f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.3f, 4.5f, 0.7f, 3f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.35f, 2.5f, 0.7f, 1.5f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.4f, -0.4f, 0.8f, 0.35f, 1f, 0f)));

        // 慌张摇头：＞＜ 表情 + 冒汗 + 快速小幅摇头 + 耸肩
        routines.Add(Routine("慌张摇头", 3.2, new[] { "＞＜" },
            Track("ParamAngleX", 0f, 0f, 0.16f, 5f, 0.3f, -5f, 0.45f, 5f, 0.6f, -5f, 0.75f, 3f, 1f, 0f),
            Track("SweatBurst1", 0f, 0f, 0.2f, 1f, 0.7f, 1f, 1f, 0f),
            Track("SweatBurst2", 0f, 0f, 0.2f, 1f, 0.7f, 1f, 1f, 0f),
            Track("ShoulderY", 0f, 0f, 0.25f, 2.5f, 0.7f, 1.5f, 1f, 0f)));

        // 屑屑眼无语：屑屑眼表情 + 呆滞 + 低头 + 眉毛耷拉
        routines.Add(Routine("屑屑眼无语", 4.4, new[] { "屑屑眼" },
            Track("Param11", 0f, 0f, 0.3f, 0.7f, 0.8f, 0.6f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.35f, 4f, 0.75f, 3f, 1f, 0f),
            Track("ParamBrowLY", 0f, 0f, 0.35f, -1.2f, 0.75f, -0.8f, 1f, 0f),
            Track("ParamBrowRY", 0f, 0f, 0.35f, -1.2f, 0.75f, -0.8f, 1f, 0f)));

        // 黑脸嫌弃：黑脸表情 + 歪头 + 身体后仰 + 眼神瞟开
        routines.Add(Routine("黑脸嫌弃", 3.6, new[] { "黑脸" },
            Track("ParamAngleZ", 0f, 0f, 0.3f, -4.5f, 0.7f, -3f, 1f, 0f),
            Track("ParamBodyAngleX", 0f, 0f, 0.35f, -3f, 0.7f, -2f, 1f, 0f),
            Track("ParamEyeBallX", 0f, 0f, 0.3f, -0.5f, 0.7f, -0.4f, 1f, 0f)));

        // 流冷汗尴尬：流冷汗表情 + 转头回避 + 耸肩 + 眉毛挑起 + 流汗
        routines.Add(Routine("流冷汗尴尬", 4.4, new[] { "流冷汗" },
            Track("ParamAngleX", 0f, 0f, 0.3f, 8f, 0.7f, 7f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.3f, 3f, 0.7f, 2f, 1f, 0f),
            Track("ShoulderY", 0f, 0f, 0.3f, 3f, 0.65f, 2.2f, 1f, 0f),
            Track("ParamBrowLY", 0f, 0f, 0.3f, 1.2f, 0.7f, 0.8f, 1f, 0f),
            Track("ParamBrowRY", 0f, 0f, 0.3f, 1.2f, 0.7f, 0.8f, 1f, 0f),
            Track("Sweat1", 0f, 0f, 0.25f, 1f, 0.8f, 1f, 1f, 0f)));

        // 假哭流泪：流泪表情 + 双眼泪滴开关 + 泪花闪烁 + 低头
        routines.Add(Routine("假哭流泪", 4.8, new[] { "流泪" },
            Track("RCryKey", 0f, 0f, 0.2f, 1f, 0.85f, 1f, 1f, 0f),
            Track("LCryKey", 0f, 0f, 0.2f, 1f, 0.85f, 1f, 1f, 0f),
            Track("Param31", 0f, 0f, 0.3f, 1f, 0.8f, 0.8f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.35f, 4.5f, 0.75f, 3.5f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.35f, 2f, 0.75f, -2f, 1f, 0f)));

        // 眼冒金星：星星闪烁 + 晕乎乎摇摆 + 屑屑眼
        routines.Add(Routine("眼冒金星", 4.6, new[] { "屑屑眼" },
            Track("Startwinkle", 0f, 0f, 0.2f, 1f, 0.85f, 1f, 1f, 0f),
            Track("Startwinkle2", 0f, 0f, 0.2f, 1f, 0.85f, 1f, 1f, 0f),
            Track("ParamAngleZ", 0f, 0f, 0.25f, 3.5f, 0.5f, -3.5f, 0.75f, 3f, 1f, 0f),
            Track("ParamAngleX", 0f, 0f, 0.3f, 2.5f, 0.55f, -2.5f, 0.8f, 1.5f, 1f, 0f)));

        // 吓一跳：SHOCK EYE + Shock 耳尾炸毛 + 后仰 + 瞪大眼（纯参数，不带表情）
        routines.Add(Routine("吓一跳", 2.8, Array.Empty<string>(),
            Track("SHOCKEYE", 0f, 0f, 0.12f, 1f, 0.8f, 1f, 1f, 0f),
            Track("ShockR", 0f, 0f, 0.12f, 1f, 0.8f, 1f, 1f, 0f),
            Track("ShockL", 0f, 0f, 0.12f, 1f, 0.8f, 1f, 1f, 0f),
            Track("ParamAngleY", 0f, 0f, 0.14f, -6f, 0.5f, -4f, 1f, 0f),
            Track("ParamBodyAngleY", 0f, 0f, 0.14f, -5f, 0.55f, -3f, 1f, 0f),
            Track("ParamEyeLOpen", 1f, 1f, 0.12f, 1.4f, 0.85f, 1.3f, 1f, 1f),
            Track("ParamEyeROpen", 1f, 1f, 0.12f, 1.4f, 0.85f, 1.3f, 1f, 1f)));

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
