using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 待机行为：自动眨眼（随机间隔）、呼吸（ParamBreath 正弦）、轻微摇摆（AngleZ / BodyAngleZ 小幅度）。
/// 都可以由运行时开关关掉；摇摆量通过 <see cref="SwayAngleZ"/> / <see cref="SwayBodyAngleZ"/>
/// 交给眼睛跟随那一层一起写进参数（避免两处抢写同一个参数）。
/// </summary>
internal sealed class IdleBehavior
{
    /// <summary>一次眨眼的闭眼时长（秒）。</summary>
    private const double BlinkCloseSeconds = 0.07;

    /// <summary>一次眨眼的睁眼时长（秒）。</summary>
    private const double BlinkOpenSeconds = 0.14;

    /// <summary>呼吸周期（秒）。</summary>
    private const double BreathPeriodSeconds = 3.2;

    /// <summary>头部左右轻摇幅度（AngleZ 角度）。</summary>
    private const float SwayAngleZAmplitude = 1.1f;

    /// <summary>身体左右轻摇幅度（ParamBodyAngleZ 角度）。</summary>
    private const float SwayBodyAngleZAmplitude = 1.6f;

    private readonly Random _random;
    private readonly CubismParameter[] _eyeParameters;
    private readonly float[] _eyeDefaults;
    private readonly float[] _eyeMinimums;
    private readonly CubismParameter? _breathParameter;
    private readonly float _breathMinimum;
    private readonly float _breathMaximum;

    private bool _blinking;
    private double _blinkStartTime;
    private double _nextBlinkTime;
    private double _blinkAmount;

    public IdleBehavior(CubismModel model, IReadOnlyList<string> eyeBlinkParameterIds, int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();

        // model3.json 没声明 EyeBlink 组时，按 Cubism 约定名兜底
        var ids = new List<string>();
        foreach (var id in eyeBlinkParameterIds) ids.Add(id);
        if (ids.Count == 0)
        {
            ids.Add("ParamEyeLOpen");
            ids.Add("ParamEyeROpen");
        }

        var parameters = new List<CubismParameter>();
        foreach (var id in ids)
        {
            var parameter = model.FindParameter(id);
            if (parameter is not null) parameters.Add(parameter);
        }

        _eyeParameters = parameters.ToArray();
        _eyeDefaults = new float[_eyeParameters.Length];
        _eyeMinimums = new float[_eyeParameters.Length];
        for (var i = 0; i < _eyeParameters.Length; i++)
        {
            _eyeDefaults[i] = _eyeParameters[i].DefaultValue;
            _eyeMinimums[i] = _eyeParameters[i].Minimum;
        }

        _breathParameter = model.FindParameter("ParamBreath");
        _breathMinimum = _breathParameter?.Minimum ?? 0.0f;
        _breathMaximum = _breathParameter?.Maximum ?? 1.0f;
        _nextBlinkTime = RandomBlinkInterval();
    }

    /// <summary>头部轻摇的 AngleZ 偏移（角度）。</summary>
    public float SwayAngleZ { get; private set; }

    /// <summary>身体轻摇的 ParamBodyAngleZ 偏移（角度）。</summary>
    public float SwayBodyAngleZ { get; private set; }

    /// <summary>
    /// 推进待机行为并写参数（眨眼 / 呼吸直接写；摇摆只算出偏移，由调用方和眼睛跟随一起写）。
    /// </summary>
    public void Update(double time, double deltaSeconds, bool autoBlink, bool breathe, bool sway)
    {
        if (deltaSeconds <= 0) return;

        UpdateBlink(time, autoBlink);
        UpdateBreath(time, breathe);
        UpdateSway(time, sway);
    }

    /// <summary>重置眨眼 / 摇摆状态（重置参数或重新开待机时用）。</summary>
    public void Reset(double time)
    {
        _blinking = false;
        _blinkAmount = 0;
        _nextBlinkTime = time + RandomBlinkInterval();
        SwayAngleZ = 0;
        SwayBodyAngleZ = 0;
    }

    private void UpdateBlink(double time, bool enabled)
    {
        if (!enabled)
        {
            _blinking = false;
            _blinkAmount = 0;
            return;
        }

        if (_blinking)
        {
            var elapsed = time - _blinkStartTime;
            var total = BlinkCloseSeconds + BlinkOpenSeconds;
            if (elapsed >= total)
            {
                _blinking = false;
                _blinkAmount = 0;
                _nextBlinkTime = time + RandomBlinkInterval();
            }
            else if (elapsed < BlinkCloseSeconds)
            {
                _blinkAmount = MotionPlayer.EasingSine(elapsed / BlinkCloseSeconds);
            }
            else
            {
                _blinkAmount = 1.0 - MotionPlayer.EasingSine((elapsed - BlinkCloseSeconds) / BlinkOpenSeconds);
            }
        }
        else if (time >= _nextBlinkTime)
        {
            _blinking = true;
            _blinkStartTime = time;
            _blinkAmount = 0;
        }

        if (_blinkAmount <= 0) return;

        var amount = (float)_blinkAmount;
        for (var i = 0; i < _eyeParameters.Length; i++)
        {
            // 闭眼：从默认值（通常 1）往最小值（通常 0）走
            _eyeParameters[i].Value = _eyeDefaults[i] + ((_eyeMinimums[i] - _eyeDefaults[i]) * amount);
        }
    }

    private void UpdateBreath(double time, bool enabled)
    {
        if (!enabled || _breathParameter is null) return;
        var phase = (time % BreathPeriodSeconds) / BreathPeriodSeconds * Math.PI * 2.0;
        var value = (float)(0.5 - (0.5 * Math.Cos(phase)));
        _breathParameter.Value = _breathMinimum + ((_breathMaximum - _breathMinimum) * value);
    }

    private void UpdateSway(double time, bool enabled)
    {
        if (!enabled)
        {
            SwayAngleZ = 0;
            SwayBodyAngleZ = 0;
            return;
        }

        SwayAngleZ = MathF.Sin((float)(time * 0.6)) * SwayAngleZAmplitude;
        SwayBodyAngleZ = MathF.Sin((float)((time * 0.47) + 1.2)) * SwayBodyAngleZAmplitude;
    }

    private double RandomBlinkInterval() => 2.5 + (_random.NextDouble() * 4.0);
}
