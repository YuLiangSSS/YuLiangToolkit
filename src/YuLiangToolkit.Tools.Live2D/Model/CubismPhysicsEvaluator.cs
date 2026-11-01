using System.Numerics;
using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 钟摆物理求值器：<c>CubismNativeFramework/src/Physics/CubismPhysics.cpp</c>（Cubism 5.0）的逐函数移植。
///
/// <para>每个物理子组：输入参数按 Position / Angle 归一化并加权 → 旋转总位移 → 更新钟摆粒子链
/// （重力 / 风 / 阻尼 / 平滑）→ 从粒子链读输出 → 写回目标参数。</para>
///
/// <para><b>与官方的对应</b>：常量 AirResistance=5、MaximumWeight=100、MovementThreshold=0.001、
/// MaxDeltaTime=5；物理按 <c>Meta.Fps</c> 定步长积分，帧间用上一次结果线性插值（官方 Interpolate）。</para>
///
/// <para><b>线程</b>：非线程安全，只在 UI 线程用。</para>
/// </summary>
internal sealed class CubismPhysicsEvaluator
{
    /// <summary>空气阻力常数（官方 AirResistance）。</summary>
    private const float AirResistance = 5.0f;

    /// <summary>输入 / 输出权重满值（官方 MaximumWeight）。</summary>
    private const float MaximumWeight = 100.0f;

    /// <summary>位移阈值（官方 MovementThreshold）。</summary>
    private const float MovementThreshold = 0.001f;

    /// <summary>单帧允许累计的最大物理时间（官方 MaxDeltaTime）。</summary>
    private const float MaxDeltaTime = 5.0f;

    private const float Pi = MathF.PI;

    private readonly CubismModel _model;
    private readonly PhysicsRig _rig;
    private readonly CubismParameter[] _parameters;
    private readonly float[] _minimums;
    private readonly float[] _maximums;
    private readonly float[] _defaults;
    private readonly float[] _parameterCaches;
    private readonly float[] _parameterInputCaches;
    private readonly float[][] _currentRigOutputs;
    private readonly float[][] _previousRigOutputs;

    private double _currentRemainTime;
    private bool _inputCachesInitialized;

    public CubismPhysicsEvaluator(CubismModel model, PhysicsRig rig)
    {
        _model = model;
        _rig = rig;

        _parameters = model.Parameters;
        var count = _parameters.Length;
        _minimums = new float[count];
        _maximums = new float[count];
        _defaults = new float[count];
        _parameterCaches = new float[count];
        _parameterInputCaches = new float[count];
        for (var i = 0; i < count; i++)
        {
            _minimums[i] = _parameters[i].Minimum;
            _maximums[i] = _parameters[i].Maximum;
            _defaults[i] = _parameters[i].DefaultValue;
        }

        // 把输入 / 输出里的参数 Id 解析成模型参数下标；解析不到的跳过并留日志
        foreach (var input in rig.Inputs)
        {
            input.SourceParameterIndex = model.FindParameter(input.SourceId)?.Index ?? -1;
            if (input.SourceParameterIndex < 0) UnresolvedInputs++;
        }

        foreach (var output in rig.Outputs)
        {
            output.DestinationParameterIndex = model.FindParameter(output.DestinationId)?.Index ?? -1;
            if (output.DestinationParameterIndex < 0) UnresolvedOutputs++;
        }

        _currentRigOutputs = new float[rig.Settings.Length][];
        _previousRigOutputs = new float[rig.Settings.Length][];
        for (var i = 0; i < rig.Settings.Length; i++)
        {
            _currentRigOutputs[i] = new float[rig.Settings[i].OutputCount];
            _previousRigOutputs[i] = new float[rig.Settings[i].OutputCount];
        }

        InitializeParticles();
        ResetCaches();
    }

    /// <summary>模型里找不到的输入参数数量（应为 0）。</summary>
    public int UnresolvedInputs { get; }

    /// <summary>模型里找不到的输出参数数量（应为 0）。</summary>
    public int UnresolvedOutputs { get; }

    /// <summary>物理子组数量。</summary>
    public int SubRigCount => _rig.Settings.Length;

    /// <summary>物理粒子总数。</summary>
    public int ParticleCount => _rig.Particles.Length;

    /// <summary>物理输出参数数量。</summary>
    public int OutputCount => _rig.Outputs.Length;

    /// <summary>物理输出配置（模型核心内部用：收集受管参数）。</summary>
    public PhysicsOutput[] Outputs => _rig.Outputs;

    /// <summary>物理帧率（physics3.json 的 Meta.Fps；0 = 按真实帧时间积分）。</summary>
    public float Fps => _rig.Fps;

    /// <summary>
    /// 推进物理。<paramref name="strength"/> 是输出权重缩放（0 = 不影响参数，1 = 正常物理）。
    /// </summary>
    public void Evaluate(double deltaSeconds, float strength)
    {
        if (deltaSeconds <= 0) return;
        if (_parameters.Length == 0) return;

        if (strength < 0) strength = 0;

        _currentRemainTime += deltaSeconds;
        if (_currentRemainTime > MaxDeltaTime)
        {
            // 官方同款：累计太久直接清零，避免一次算上百步
            _currentRemainTime = 0;
        }

        var physicsDeltaTime = _rig.Fps > 0 ? 1.0f / _rig.Fps : (float)deltaSeconds;
        if (physicsDeltaTime <= 0) return;

        while (_currentRemainTime >= physicsDeltaTime)
        {
            Step(physicsDeltaTime, strength);
            _currentRemainTime -= physicsDeltaTime;
        }

        var alpha = (float)(_currentRemainTime / physicsDeltaTime);
        Interpolate(alpha, strength);
    }

    /// <summary>
    /// 让物理先收敛一次（加载完调用，避免第一帧从初始垂挂位置甩动）。
    /// 官方 <c>CubismPhysics::Stabilization</c> 的移植。
    /// </summary>
    public void Stabilize()
    {
        if (_parameters.Length == 0) return;

        for (var settingIndex = 0; settingIndex < _rig.Settings.Length; settingIndex++)
        {
            var setting = _rig.Settings[settingIndex];
            var outputs = _rig.Outputs;
            var particles = _rig.Particles;
            var totalAngle = 0.0f;
            var totalTranslation = Vector2.Zero;

            for (var i = 0; i < setting.InputCount; i++)
            {
                var input = _rig.Inputs[setting.BaseInputIndex + i];
                var index = input.SourceParameterIndex;
                if (index < 0) continue;
                AddNormalizedInput(input, setting.Normalization, _parameters[index].Value,
                    _minimums[index], _maximums[index], _defaults[index], ref totalTranslation, ref totalAngle);
            }

            RotateTotalTranslation(ref totalTranslation, totalAngle);

            UpdateParticlesForStabilization(particles, setting.BaseParticleIndex, setting.ParticleCount,
                totalTranslation, totalAngle, _rig.Wind,
                MovementThreshold * setting.Normalization.PositionMaximum);

            for (var i = 0; i < setting.OutputCount; i++)
            {
                var output = outputs[setting.BaseOutputIndex + i];
                var particleIndex = output.VertexIndex;
                var destinationIndex = output.DestinationParameterIndex;
                if (destinationIndex < 0) continue;
                if (particleIndex < 1 || particleIndex >= setting.ParticleCount) continue;

                var absoluteIndex = setting.BaseParticleIndex + particleIndex;
                var translation = particles[absoluteIndex].Position - particles[absoluteIndex - 1].Position;
                var outputValue = GetOutputValue(output, translation, particles, setting.BaseParticleIndex, particleIndex);

                _currentRigOutputs[settingIndex][i] = outputValue;
                _previousRigOutputs[settingIndex][i] = outputValue;

                var parameterValue = _parameters[destinationIndex].Value;
                parameterValue = ApplyOutputValue(parameterValue, _minimums[destinationIndex], _maximums[destinationIndex],
                    outputValue, output, 1.0f);
                _parameters[destinationIndex].Value = parameterValue;
                _parameterCaches[destinationIndex] = parameterValue;
            }
        }

        // 稳定后把输入缓存对齐当前参数值，避免第一帧插值跳变
        for (var i = 0; i < _parameters.Length; i++) _parameterInputCaches[i] = _parameters[i].Value;
    }

    /// <summary>把物理状态重置回初始垂挂姿态（重开模型 / 重置参数时用）。</summary>
    public void Reset()
    {
        _currentRemainTime = 0;
        InitializeParticles();
        foreach (var outputs in _currentRigOutputs) Array.Clear(outputs);
        foreach (var outputs in _previousRigOutputs) Array.Clear(outputs);
        ResetCaches();
    }

    private void InitializeParticles()
    {
        var particles = _rig.Particles;
        foreach (var setting in _rig.Settings)
        {
            var baseIndex = setting.BaseParticleIndex;
            if (setting.ParticleCount <= 0) continue;

            particles[baseIndex].InitialPosition = Vector2.Zero;
            particles[baseIndex].LastPosition = Vector2.Zero;
            particles[baseIndex].LastGravity = new Vector2(0.0f, 1.0f);
            particles[baseIndex].Velocity = Vector2.Zero;
            particles[baseIndex].Force = Vector2.Zero;

            for (var i = 1; i < setting.ParticleCount; i++)
            {
                var index = baseIndex + i;
                var radius = new Vector2(0.0f, particles[index].Radius);
                particles[index].InitialPosition = particles[index - 1].InitialPosition + radius;
                particles[index].Position = particles[index].InitialPosition;
                particles[index].LastPosition = particles[index].InitialPosition;
                particles[index].LastGravity = new Vector2(0.0f, 1.0f);
                particles[index].Velocity = Vector2.Zero;
                particles[index].Force = Vector2.Zero;
            }
        }
    }

    private void ResetCaches()
    {
        for (var i = 0; i < _parameters.Length; i++)
        {
            _parameterCaches[i] = _parameters[i].Value;
            _parameterInputCaches[i] = _parameters[i].Value;
        }

        _inputCachesInitialized = true;
    }

    /// <summary>一个物理步（官方 <c>CubismPhysics::Evaluate</c> 里 while 循环体）。</summary>
    private void Step(float physicsDeltaTime, float strength)
    {
        // 上一次输出保留下来做帧间插值
        for (var settingIndex = 0; settingIndex < _rig.Settings.Length; settingIndex++)
        {
            var previous = _previousRigOutputs[settingIndex];
            var current = _currentRigOutputs[settingIndex];
            Array.Copy(current, previous, current.Length);
        }

        if (!_inputCachesInitialized)
        {
            for (var i = 0; i < _parameters.Length; i++) _parameterInputCaches[i] = _parameters[i].Value;
            _inputCachesInitialized = true;
        }

        // 用「上次输入缓存 → 当前参数值」线性插值出本步的输入（官方同款）
        var inputWeight = (float)(physicsDeltaTime / _currentRemainTime);
        for (var i = 0; i < _parameters.Length; i++)
        {
            _parameterCaches[i] = (_parameterInputCaches[i] * (1.0f - inputWeight)) + (_parameters[i].Value * inputWeight);
            _parameterInputCaches[i] = _parameterCaches[i];
        }

        for (var settingIndex = 0; settingIndex < _rig.Settings.Length; settingIndex++)
        {
            var setting = _rig.Settings[settingIndex];
            var totalAngle = 0.0f;
            var totalTranslation = Vector2.Zero;

            for (var i = 0; i < setting.InputCount; i++)
            {
                var input = _rig.Inputs[setting.BaseInputIndex + i];
                var index = input.SourceParameterIndex;
                if (index < 0) continue;
                AddNormalizedInput(input, setting.Normalization, _parameterCaches[index],
                    _minimums[index], _maximums[index], _defaults[index], ref totalTranslation, ref totalAngle);
            }

            RotateTotalTranslation(ref totalTranslation, totalAngle);

            UpdateParticles(_rig.Particles, setting.BaseParticleIndex, setting.ParticleCount,
                totalTranslation, totalAngle, _rig.Wind,
                MovementThreshold * setting.Normalization.PositionMaximum,
                physicsDeltaTime, AirResistance);

            for (var i = 0; i < setting.OutputCount; i++)
            {
                var output = _rig.Outputs[setting.BaseOutputIndex + i];
                var particleIndex = output.VertexIndex;
                var destinationIndex = output.DestinationParameterIndex;
                if (destinationIndex < 0) continue;
                if (particleIndex < 1 || particleIndex >= setting.ParticleCount) continue;

                var absoluteIndex = setting.BaseParticleIndex + particleIndex;
                var translation = _rig.Particles[absoluteIndex].Position - _rig.Particles[absoluteIndex - 1].Position;
                var outputValue = GetOutputValue(output, translation, _rig.Particles, setting.BaseParticleIndex, particleIndex);

                _currentRigOutputs[settingIndex][i] = outputValue;
                _parameterCaches[destinationIndex] = ApplyOutputValue(
                    _parameterCaches[destinationIndex],
                    _minimums[destinationIndex], _maximums[destinationIndex],
                    outputValue, output, strength);
            }
        }
    }

    /// <summary>帧间插值（官方 <c>CubismPhysics::Interpolate</c>）：把插值结果写回模型参数。</summary>
    private void Interpolate(float alpha, float strength)
    {
        for (var settingIndex = 0; settingIndex < _rig.Settings.Length; settingIndex++)
        {
            var setting = _rig.Settings[settingIndex];
            for (var i = 0; i < setting.OutputCount; i++)
            {
                var output = _rig.Outputs[setting.BaseOutputIndex + i];
                var destinationIndex = output.DestinationParameterIndex;
                if (destinationIndex < 0) continue;

                var value = (_previousRigOutputs[settingIndex][i] * (1.0f - alpha)) +
                            (_currentRigOutputs[settingIndex][i] * alpha);
                _parameters[destinationIndex].Value = ApplyOutputValue(
                    _parameters[destinationIndex].Value,
                    _minimums[destinationIndex], _maximums[destinationIndex],
                    value, output, strength);
            }
        }
    }

    // ---------- 输入 / 输出换算（官方 namespace 里的自由函数） ----------

    private static void AddNormalizedInput(PhysicsInput input, PhysicsNormalization normalization, float value,
        float parameterMinimum, float parameterMaximum, float parameterDefault,
        ref Vector2 totalTranslation, ref float totalAngle)
    {
        var weight = input.Weight / MaximumWeight;
        switch (input.Type)
        {
            case PhysicsValueType.X:
                totalTranslation.X += NormalizeParameterValue(value, parameterMinimum, parameterMaximum, parameterDefault,
                    normalization.PositionMinimum, normalization.PositionMaximum, normalization.PositionDefault,
                    input.Reflect) * weight;
                break;

            case PhysicsValueType.Y:
                totalTranslation.Y += NormalizeParameterValue(value, parameterMinimum, parameterMaximum, parameterDefault,
                    normalization.PositionMinimum, normalization.PositionMaximum, normalization.PositionDefault,
                    input.Reflect) * weight;
                break;

            default:
                totalAngle += NormalizeParameterValue(value, parameterMinimum, parameterMaximum, parameterDefault,
                    normalization.AngleMinimum, normalization.AngleMaximum, normalization.AngleDefault,
                    input.Reflect) * weight;
                break;
        }
    }

    /// <summary>官方同款：把总位移按 -totalAngle 旋转（注意官方代码第二行用旋转后的 X，这里保持一致）。</summary>
    private static void RotateTotalTranslation(ref Vector2 totalTranslation, float totalAngle)
    {
        var radian = DegreesToRadian(-totalAngle);
        var cos = MathF.Cos(radian);
        var sin = MathF.Sin(radian);
        var x = (totalTranslation.X * cos) - (totalTranslation.Y * sin);
        var y = (x * sin) + (totalTranslation.Y * cos);
        totalTranslation = new Vector2(x, y);
    }

    private static float GetOutputValue(PhysicsOutput output, Vector2 translation,
        PhysicsParticle[] particles, int baseParticleIndex, int particleIndex)
    {
        switch (output.Type)
        {
            case PhysicsValueType.X:
            {
                var value = translation.X;
                return output.Reflect ? value * -1.0f : value;
            }

            case PhysicsValueType.Y:
            {
                var value = translation.Y;
                return output.Reflect ? value * -1.0f : value;
            }

            default:
            {
                Vector2 parentGravity;
                if (particleIndex >= 2)
                {
                    var absolute = baseParticleIndex + particleIndex;
                    parentGravity = particles[absolute - 1].Position - particles[absolute - 2].Position;
                }
                else
                {
                    parentGravity = DefaultGravity * -1.0f;
                }

                var value = DirectionToRadian(parentGravity, translation);
                return output.Reflect ? value * -1.0f : value;
            }
        }
    }

    /// <summary>输出缩放：官方 PhysicsOutput 里 Angle 用 AngleScale，X/Y 用 TranslationScale（JSON 里没有该字段，恒 0）。</summary>
    private static float GetOutputScale(PhysicsOutput output) => output.Type switch
    {
        PhysicsValueType.X => 0.0f,
        PhysicsValueType.Y => 0.0f,
        _ => output.AngleScale,
    };

    /// <summary>官方 <c>UpdateOutputParameterValue</c>：缩放 → 夹到参数范围 → 按权重和当前值混合。</summary>
    private static float ApplyOutputValue(float parameterValue, float parameterMinimum, float parameterMaximum,
        float translation, PhysicsOutput output, float strength)
    {
        var value = translation * GetOutputScale(output);
        if (!float.IsFinite(value)) return parameterValue;

        if (value < parameterMinimum)
        {
            if (value < output.ValueBelowMinimum) output.ValueBelowMinimum = value;
            value = parameterMinimum;
        }
        else if (value > parameterMaximum)
        {
            if (value > output.ValueExceededMaximum) output.ValueExceededMaximum = value;
            value = parameterMaximum;
        }

        var weight = (output.Weight / MaximumWeight) * strength;
        if (weight < 0) weight = 0;
        else if (weight > 1) weight = 1;

        if (weight >= 1.0f) return value;
        return (parameterValue * (1.0f - weight)) + (value * weight);
    }

    // ---------- 归一化 / 数学工具 ----------

    /// <summary>官方 <c>NormalizeParameterValue</c>：把参数值按归一化范围映射（注意保持官方的取反语义）。</summary>
    private static float NormalizeParameterValue(float value, float parameterMinimum, float parameterMaximum,
        float parameterDefault, float normalizedMinimum, float normalizedMaximum, float normalizedDefault, bool isInverted)
    {
        // parameterDefault 官方也没用到（用 min/max 的中值），保留参数只为跟官方签名对齐
        _ = parameterDefault;

        var result = 0.0f;

        var maxValue = MathF.Max(parameterMaximum, parameterMinimum);
        if (maxValue < value) value = maxValue;

        var minValue = MathF.Min(parameterMaximum, parameterMinimum);
        if (minValue > value) value = minValue;

        var minNormValue = MathF.Min(normalizedMinimum, normalizedMaximum);
        var maxNormValue = MathF.Max(normalizedMinimum, normalizedMaximum);
        var middleNormValue = normalizedDefault;

        var middleValue = GetDefaultValue(minValue, maxValue);
        var paramValue = value - middleValue;

        switch (Sign(paramValue))
        {
            case 1:
            {
                var nLength = maxNormValue - middleNormValue;
                var pLength = maxValue - middleValue;
                if (pLength != 0.0f)
                {
                    result = paramValue * (nLength / pLength);
                    result += middleNormValue;
                }

                break;
            }

            case -1:
            {
                var nLength = minNormValue - middleNormValue;
                var pLength = minValue - middleValue;
                if (pLength != 0.0f)
                {
                    result = paramValue * (nLength / pLength);
                    result += middleNormValue;
                }

                break;
            }

            default:
                result = middleNormValue;
                break;
        }

        return isInverted ? result : result * -1.0f;
    }

    private static float GetRangeValue(float min, float max)
        => MathF.Abs(MathF.Max(min, max) - MathF.Min(min, max));

    private static float GetDefaultValue(float min, float max)
    {
        var minValue = MathF.Min(min, max);
        return minValue + (GetRangeValue(min, max) / 2.0f);
    }

    private static int Sign(float value)
        => value > 0.0f ? 1 : value < 0.0f ? -1 : 0;

    private static float DirectionToRadian(Vector2 from, Vector2 to)
    {
        var q1 = MathF.Atan2(to.Y, to.X);
        var q2 = MathF.Atan2(from.Y, from.X);
        var result = q1 - q2;
        while (result < -Pi) result += Pi * 2.0f;
        while (result > Pi) result -= Pi * 2.0f;
        return float.IsFinite(result) ? result : 0.0f;
    }

    private static float DegreesToRadian(float degrees) => (degrees / 180.0f) * Pi;

    /// <summary>官方 <c>CubismVector2::Normalize</c>，但对零向量返回 0（官方会得到 NaN，这里避免脏数据）。</summary>
    private static Vector2 Normalize(Vector2 value)
    {
        var length = MathF.Sqrt((value.X * value.X) + (value.Y * value.Y));
        return length > 1e-8f ? value / length : Vector2.Zero;
    }

    // ---------- 粒子更新（官方 UpdateParticles / UpdateParticlesForStabilization） ----------

    private static void UpdateParticles(PhysicsParticle[] particles, int baseIndex, int count,
        Vector2 totalTranslation, float totalAngle, Vector2 windDirection,
        float thresholdValue, float deltaTimeSeconds, float airResistance)
    {
        particles[baseIndex].Position = totalTranslation;

        var totalRadian = DegreesToRadian(totalAngle);
        var currentGravity = Normalize(new Vector2(MathF.Sin(totalRadian), MathF.Cos(totalRadian)));

        for (var i = 1; i < count; i++)
        {
            var index = baseIndex + i;
            particles[index].Force = (currentGravity * particles[index].Acceleration) + windDirection;
            particles[index].LastPosition = particles[index].Position;

            var delay = particles[index].Delay * deltaTimeSeconds * 30.0f;

            var direction = particles[index].Position - particles[index - 1].Position;
            var radian = DirectionToRadian(particles[index].LastGravity, currentGravity) / airResistance;
            var cos = MathF.Cos(radian);
            var sin = MathF.Sin(radian);

            // 官方 (C++/TS) 就是这么写的：第二行用的是旋转后的 X，保持一致
            var x = (cos * direction.X) - (direction.Y * sin);
            var y = (sin * x) + (direction.Y * cos);
            direction = new Vector2(x, y);

            particles[index].Position = particles[index - 1].Position + direction;

            var velocity = particles[index].Velocity * delay;
            var force = particles[index].Force * delay * delay;
            particles[index].Position += velocity + force;

            var newDirection = Normalize(particles[index].Position - particles[index - 1].Position);
            particles[index].Position = particles[index - 1].Position + (newDirection * particles[index].Radius);

            if (MathF.Abs(particles[index].Position.X) < thresholdValue) particles[index].Position.X = 0.0f;

            if (delay != 0.0f)
            {
                particles[index].Velocity = (particles[index].Position - particles[index].LastPosition) / delay;
                particles[index].Velocity *= particles[index].Mobility;
            }

            particles[index].Force = Vector2.Zero;
            particles[index].LastGravity = currentGravity;
        }
    }

    private static void UpdateParticlesForStabilization(PhysicsParticle[] particles, int baseIndex, int count,
        Vector2 totalTranslation, float totalAngle, Vector2 windDirection, float thresholdValue)
    {
        particles[baseIndex].Position = totalTranslation;

        var totalRadian = DegreesToRadian(totalAngle);
        var currentGravity = Normalize(new Vector2(MathF.Sin(totalRadian), MathF.Cos(totalRadian)));

        for (var i = 1; i < count; i++)
        {
            var index = baseIndex + i;
            particles[index].Force = (currentGravity * particles[index].Acceleration) + windDirection;
            particles[index].LastPosition = particles[index].Position;
            particles[index].Velocity = Vector2.Zero;

            var force = Normalize(particles[index].Force);
            force *= particles[index].Radius;
            particles[index].Position = particles[index - 1].Position + force;

            if (MathF.Abs(particles[index].Position.X) < thresholdValue) particles[index].Position.X = 0.0f;

            particles[index].Force = Vector2.Zero;
            particles[index].LastGravity = currentGravity;
        }
    }

    /// <summary>输出角度计算时用的“父重力”：官方用 <c>CubismPhysics::_options.Gravity</c>（默认 0,-1）。</summary>
    private static readonly Vector2 DefaultGravity = new(0.0f, -1.0f);
}
