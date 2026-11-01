using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>一条正在播放的动作。</summary>
internal sealed class MotionPlayback
{
    public required MotionEntry Entry { get; init; }
    public required Motion3Data Data { get; init; }

    /// <summary>每条曲线解析好的目标（参数 / 部件），播放时不再查字典。</summary>
    public required MotionCurveBinding[] Targets { get; init; }

    /// <summary>是否循环播放。</summary>
    public bool Loop { get; set; }

    /// <summary>开始播放的（运行时）绝对时间。</summary>
    public double StartTime { get; set; }

    /// <summary>淡入开始时间。</summary>
    public double FadeInStartTime { get; set; }

    public double FadeInSeconds { get; set; }

    public double FadeOutSeconds { get; set; }

    /// <summary>绝对结束时间（-1 = 不结束）。循环动作被新动作顶掉时也会被设成结束时间。</summary>
    public double EndTime { get; set; } = -1;

    public bool Finished { get; set; }

    public double Duration => Data.Duration;
}

/// <summary>曲线解析出来的目标（参数 / 部件，可能有一个或都为空）。</summary>
internal readonly struct MotionCurveBinding
{
    public MotionCurveBinding(CubismParameter? parameter, CubismPart? part)
    {
        Parameter = parameter;
        Part = part;
    }

    public CubismParameter? Parameter { get; }
    public CubismPart? Part { get; }
}

/// <summary>
/// 动作混合器：照官方 <c>CubismMotionQueueManager</c> / <c>CubismMotion</c> 的语义做「新动作淡入、旧动作淡出」。
///
/// <para>官方对循环动作每次回绕都会重触发淡入（<c>isLoopFadeIn</c> 默认 true）；
/// 本运行时为了让待机循环连续，只保留首次淡入（等价于 <c>isLoopFadeIn = false</c>）。
/// 一次性动作结尾用较短淡出（默认 0.3 秒；官方默认 1 秒，会让 1 秒出头的短动作刚播完就淡没了）。</para>
/// </summary>
internal sealed class MotionPlayer
{
    /// <summary>一次性动作 / 动作切换的默认淡入时间（秒）。</summary>
    private const double DefaultFadeInSeconds = 0.3;

    /// <summary>一次性动作结尾的默认淡出时间上限（秒）。</summary>
    private const double DefaultFadeOutSeconds = 0.3;

    private readonly List<MotionPlayback> _instances = new(2);
    private double _userTime;

    // 本帧动作写过的参数下标（下一帧 ModelRuntime 先按它复位，保证动作没覆盖的参数不残留；
    // 只被实际在播的动作写过的参数才会复位，用户手动调的其它参数不受影响）
    private int[] _touchedParameterIndices = new int[128];
    private int _touchedParameterCount;

    /// <summary>本帧动作写过的参数个数（上一帧的结果；ModelRuntime 用来做「受管参数」复位）。</summary>
    public int TouchedParameterCount => _touchedParameterCount;

    /// <summary>本帧动作写过的参数下标（上一帧的结果）。</summary>
    public int GetTouchedParameterIndex(int index) => _touchedParameterIndices[index];

    /// <summary>有没有正在播的动作。</summary>
    public bool HasActive => _instances.Count > 0;

    /// <summary>最新开始播放的那个动作（= UI 说的「当前动作」）。</summary>
    public MotionPlayback? Current => _instances.Count > 0 ? _instances[^1] : null;

    /// <summary>当前动作已播放时间（秒，未开始为 0）。</summary>
    public double CurrentTime
    {
        get
        {
            var current = Current;
            if (current is null) return 0;
            var time = _userTime - current.StartTime;
            return time < 0 ? 0 : time > current.Duration ? current.Duration : time;
        }
    }

    /// <summary>当前动作是否循环。</summary>
    public bool CurrentLoop => Current?.Loop ?? false;

    /// <summary>播放一个动作（已有动作会被淡出顶掉）。</summary>
    /// <param name="fadeSeconds">
    /// 淡入 / 交叉淡化秒数；&lt; 0 = 用动作自带的 Meta.FadeInTime（没有就 0.3 秒）；0 = 立即切换。
    /// </param>
    public void Play(CubismModel model, MotionEntry entry, Motion3Data data, bool loop, double fadeSeconds = -1)
    {
        var fadeIn = fadeSeconds >= 0 ? fadeSeconds : ResolveFadeIn(data);

        // 旧动作：按新动作的淡入时间交叉淡出（已经更早结束的保持原样）
        foreach (var instance in _instances)
        {
            if (instance.Finished) continue;
            var endTime = _userTime + Math.Max(fadeIn, 0.0001);
            if (instance.EndTime < 0 || instance.EndTime > endTime)
            {
                instance.EndTime = endTime;
                instance.FadeOutSeconds = Math.Max(fadeIn, 0.0001);
            }
        }

        _instances.Add(new MotionPlayback
        {
            Entry = entry,
            Data = data,
            Targets = ResolveBindings(model, data),
            Loop = loop,
            StartTime = _userTime,
            FadeInStartTime = _userTime,
            FadeInSeconds = fadeIn,
            FadeOutSeconds = ResolveFadeOut(data, loop),
            EndTime = loop ? -1 : _userTime + data.Duration,
        });

        // 极端情况下（连续快速点动作）别让队列无限长
        while (_instances.Count > 3)
        {
            var oldest = _instances[0];
            if (oldest.EndTime < 0 || oldest.EndTime > _userTime + 0.1)
            {
                oldest.EndTime = _userTime + 0.1;
                oldest.FadeOutSeconds = 0.1;
            }

            _instances.RemoveAt(0);
        }
    }

    /// <summary>停止当前动作（快速淡出，不再推进）。</summary>
    public void Stop(double fadeSeconds = 0.25)
    {
        if (_instances.Count == 0) return;
        var duration = fadeSeconds > 0 ? fadeSeconds : 0.0001;
        var endTime = _userTime + duration;
        foreach (var instance in _instances)
        {
            if (instance.Finished) continue;
            instance.EndTime = endTime;
            instance.FadeOutSeconds = duration;
        }
    }

    /// <summary>立刻清空（不淡出）。</summary>
    public void Clear() => _instances.Clear();

    /// <summary>推进一帧：求值 + 把参数 / 部件不透明度写进模型。</summary>
    public void Update(CubismModel model, double deltaSeconds,
        IReadOnlyList<string>? eyeBlinkParameterIds, IReadOnlyList<string>? lipSyncParameterIds)
    {
        if (deltaSeconds <= 0) return;
        _userTime += deltaSeconds;
        _touchedParameterCount = 0;

        for (var i = 0; i < _instances.Count; i++)
        {
            UpdateInstance(model, _instances[i], eyeBlinkParameterIds, lipSyncParameterIds);
        }

        for (var i = _instances.Count - 1; i >= 0; i--)
        {
            if (_instances[i].Finished) _instances.RemoveAt(i);
        }
    }

    private void UpdateInstance(CubismModel model, MotionPlayback instance,
        IReadOnlyList<string>? eyeBlinkParameterIds, IReadOnlyList<string>? lipSyncParameterIds)
    {
        if (instance.Finished) return;

        var data = instance.Data;
        var timeOffset = _userTime - instance.StartTime;
        if (timeOffset < 0) timeOffset = 0;

        var fadeIn = instance.FadeInSeconds <= 0
            ? 1f
            : EasingSine((_userTime - instance.FadeInStartTime) / instance.FadeInSeconds);
        var fadeOut = instance.FadeOutSeconds <= 0 || instance.EndTime < 0
            ? 1f
            : EasingSine((instance.EndTime - _userTime) / instance.FadeOutSeconds);
        var fadeWeight = fadeIn * fadeOut;

        // 循环：在 Duration + 1/Fps 处回绕（官方 V2 行为），末尾用 CorrectEndPoint 补回首点
        var duration = data.Duration;
        var isCorrection = instance.Loop;
        var time = (float)timeOffset;
        if (instance.Loop)
        {
            duration += 1.0f / data.Fps;
            while (time > duration) time -= duration;
        }
        else if (time > data.Duration)
        {
            time = data.Duration;
        }

        var eyeBlinkValue = float.MaxValue;
        var lipSyncValue = float.MaxValue;
        var curves = data.Curves;

        // 1) Model 曲线：EyeBlink / LipSync（Opacity 本运行时没有模型级透明度接口，忽略）
        for (var i = 0; i < curves.Length; i++)
        {
            var curve = curves[i];
            if (curve.Target != MotionCurveTarget.Model) continue;
            var value = data.EvaluateCurve(i, time, isCorrection, duration);
            if (string.Equals(curve.Id, "EyeBlink", StringComparison.Ordinal)) eyeBlinkValue = value;
            else if (string.Equals(curve.Id, "LipSync", StringComparison.Ordinal)) lipSyncValue = value;
        }

        // 2) Parameter 曲线（眨眼 / 口型命中用位掩码记录，参数一般不超过 32 个）
        var eyeBlinkHit = 0UL;
        var lipSyncHit = 0UL;

        for (var i = 0; i < curves.Length; i++)
        {
            var curve = curves[i];
            if (curve.Target != MotionCurveTarget.Parameter) continue;

            var target = instance.Targets[i].Parameter;
            if (target is null) continue;

            var sourceValue = target.Value;
            var value = data.EvaluateCurve(i, time, isCorrection, duration);

            if (eyeBlinkValue != float.MaxValue && eyeBlinkParameterIds is { Count: > 0 })
            {
                for (var e = 0; e < eyeBlinkParameterIds.Count && e < 64; e++)
                {
                    if (!string.Equals(eyeBlinkParameterIds[e], curve.Id, StringComparison.Ordinal)) continue;
                    value *= eyeBlinkValue;
                    eyeBlinkHit |= 1UL << e;
                    break;
                }
            }

            if (lipSyncValue != float.MaxValue && lipSyncParameterIds is { Count: > 0 })
            {
                for (var l = 0; l < lipSyncParameterIds.Count && l < 64; l++)
                {
                    if (!string.Equals(lipSyncParameterIds[l], curve.Id, StringComparison.Ordinal)) continue;
                    value += lipSyncValue;
                    lipSyncHit |= 1UL << l;
                    break;
                }
            }

            if (curve.FadeInTime < 0 && curve.FadeOutTime < 0)
            {
                // 没有逐参数淡入淡出：用动作整体的淡入淡出权重
                target.Value = sourceValue + ((value - sourceValue) * fadeWeight);
                MarkTouched(target);
            }
            else
            {
                var fin = curve.FadeInTime < 0
                    ? fadeIn
                    : curve.FadeInTime == 0
                        ? 1f
                        : EasingSine((_userTime - instance.FadeInStartTime) / curve.FadeInTime);
                var fout = curve.FadeOutTime < 0
                    ? fadeOut
                    : curve.FadeOutTime == 0 || instance.EndTime < 0
                        ? 1f
                        : EasingSine((instance.EndTime - _userTime) / curve.FadeOutTime);
                target.Value = sourceValue + ((value - sourceValue) * (fin * fout));
                MarkTouched(target);
            }
        }

        // 3) 没被动作曲线覆盖的眨眼 / 口型参数：用 Model 曲线的值补上
        if (eyeBlinkValue != float.MaxValue && eyeBlinkParameterIds is { Count: > 0 })
        {
            for (var e = 0; e < eyeBlinkParameterIds.Count && e < 64; e++)
            {
                if ((eyeBlinkHit & (1UL << e)) != 0) continue;
                var parameter = model.FindParameter(eyeBlinkParameterIds[e]);
                if (parameter is null) continue;
                var sourceValue = parameter.Value;
                parameter.Value = sourceValue + ((eyeBlinkValue - sourceValue) * fadeWeight);
                MarkTouched(parameter);
            }
        }

        if (lipSyncValue != float.MaxValue && lipSyncParameterIds is { Count: > 0 })
        {
            for (var l = 0; l < lipSyncParameterIds.Count && l < 64; l++)
            {
                if ((lipSyncHit & (1UL << l)) != 0) continue;
                var parameter = model.FindParameter(lipSyncParameterIds[l]);
                if (parameter is null) continue;
                var sourceValue = parameter.Value;
                parameter.Value = sourceValue + ((lipSyncValue - sourceValue) * fadeWeight);
                MarkTouched(parameter);
            }
        }

        // 4) PartOpacity 曲线（官方用参数下标求值；找不到参数时按部件不透明度处理）
        for (var i = 0; i < curves.Length; i++)
        {
            var curve = curves[i];
            if (curve.Target != MotionCurveTarget.PartOpacity) continue;
            var value = data.EvaluateCurve(i, time, isCorrection, duration);
            var binding = instance.Targets[i];
            if (binding.Parameter is { } parameter)
            {
                parameter.Value = value;
                MarkTouched(parameter);
            }
            else if (binding.Part is { } part)
            {
                part.Opacity = value;
            }
        }

        // 5) 结束判定：不管循环与否，只要被设置了结束时间（StopMotion / 被新动作顶掉 / 一次性动作播完）就到点结束
        if (instance.EndTime >= 0 && _userTime >= instance.EndTime)
        {
            instance.Finished = true;
            return;
        }

        if (instance.Loop && timeOffset >= duration)
        {
            // 把起点挪到当前循环的起点（不重置淡入，保证循环连续）
            instance.StartTime = _userTime - time;
        }
    }

    /// <summary>记录「这一帧动作写了这个参数」（扩容只在首次超出时发生，稳态 0 分配）。</summary>
    private void MarkTouched(CubismParameter parameter)
    {
        if (_touchedParameterCount == _touchedParameterIndices.Length)
        {
            Array.Resize(ref _touchedParameterIndices, _touchedParameterIndices.Length * 2);
        }

        _touchedParameterIndices[_touchedParameterCount++] = parameter.Index;
    }

    /// <summary>官方 <c>CubismMath::GetEasingSine</c>：0 → 0、1 → 1 的正弦缓动。</summary>
    internal static float EasingSine(double value)
    {
        if (value < 0) return 0;
        if (value > 1) return 1;
        return (float)(0.5 - (0.5 * Math.Cos(value * Math.PI)));
    }

    private static MotionCurveBinding[] ResolveBindings(CubismModel model, Motion3Data data)
    {
        var bindings = new MotionCurveBinding[data.Curves.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            var curve = data.Curves[i];
            bindings[i] = curve.Target switch
            {
                MotionCurveTarget.Parameter => new MotionCurveBinding(model.FindParameter(curve.Id), null),
                MotionCurveTarget.PartOpacity => new MotionCurveBinding(model.FindParameter(curve.Id), model.FindPart(curve.Id)),
                _ => default,
            };
        }

        return bindings;
    }

    private static double ResolveFadeIn(Motion3Data data)
        => data.HasFadeInTime ? Math.Clamp(data.FadeInTime, 0.0, 2.0) : DefaultFadeInSeconds;

    private static double ResolveFadeOut(Motion3Data data, bool loop)
    {
        if (loop) return 0;
        if (data.HasFadeOutTime) return Math.Clamp(data.FadeOutTime, 0.0, 1.0);
        return Math.Min(DefaultFadeOutSeconds, data.Duration * 0.5);
    }
}
