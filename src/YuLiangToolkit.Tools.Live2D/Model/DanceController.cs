using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 舞蹈控制器（程序化循环，不依赖动作文件）：4 种模式，写左右臂 / 肩膀 / 头部与身体「物理输入」参数
/// （AngleX/Y/Z、PositionX/Y/Z、LeanInput），**不写物理输出**（ParamBodyAngle* 交给物理求值，避免出现硬扭腰）。
/// 停止时 0.25 秒整体权重淡出回默认；相位连续推进，速度 0.5~2.0 可调。
/// </summary>
internal sealed class DanceController
{
    public const string ModeSway = "轻快摇摆";
    public const string ModeWave = "挥手舞";
    public const string ModeSpin = "摇摆转圈";
    public const string ModeShimmy = "酷炫抖肩";

    private static readonly string[] ModeList = { ModeSway, ModeWave, ModeSpin, ModeShimmy };

    private readonly CubismModel _model;
    private readonly CubismParameter? _lArm;
    private readonly CubismParameter? _lForearm;
    private readonly CubismParameter? _lWrist;
    private readonly CubismParameter? _lHand;
    private readonly CubismParameter? _lHand2;
    private readonly CubismParameter? _rArm;
    private readonly CubismParameter? _rForearm;
    private readonly CubismParameter? _rWrist;
    private readonly CubismParameter? _rHand;
    private readonly CubismParameter? _rHand2;
    private readonly CubismParameter? _shoulderX;
    private readonly CubismParameter? _shoulderY;
    private readonly CubismParameter? _shoulderZ;
    private readonly CubismParameter? _shoulderYy;
    private readonly CubismParameter? _angleX;
    private readonly CubismParameter? _angleY;
    private readonly CubismParameter? _angleZ;
    private readonly CubismParameter? _positionX;
    private readonly CubismParameter? _positionY;
    private readonly CubismParameter? _positionZ;
    private readonly CubismParameter? _leanInput;
    private readonly CubismParameter? _paramAngleX;
    private readonly CubismParameter? _paramAngleY;
    private readonly CubismParameter? _paramAngleZ;

    private int[] _touchedIndices = new int[40];
    private int _touchedCount;

    private string? _mode;
    private double _speed = 1.0;
    private double _phase;
    private float _weight;
    private bool _stopping;

    public DanceController(CubismModel model)
    {
        _model = model;
        _lArm = model.FindParameter("LArm");
        _lForearm = model.FindParameter("LForearm");
        _lWrist = model.FindParameter("LWrist");
        _lHand = model.FindParameter("LHand");
        _lHand2 = model.FindParameter("LHand2");
        _rArm = model.FindParameter("RArm");
        _rForearm = model.FindParameter("RForearm");
        _rWrist = model.FindParameter("RWrist");
        _rHand = model.FindParameter("RHand");
        _rHand2 = model.FindParameter("RHand2");
        _shoulderX = model.FindParameter("ShoulderX");
        _shoulderY = model.FindParameter("ShoulderY");
        _shoulderZ = model.FindParameter("ShoulderZ");
        _shoulderYy = model.FindParameter("ShoulderYy");
        _angleX = model.FindParameter("AngleX");
        _angleY = model.FindParameter("AngleY");
        _angleZ = model.FindParameter("AngleZ");
        _positionX = model.FindParameter("PositionX");
        _positionY = model.FindParameter("PositionY");
        _positionZ = model.FindParameter("PositionZ");
        _leanInput = model.FindParameter("LeanInput");
        _paramAngleX = model.FindParameter("ParamAngleX");
        _paramAngleY = model.FindParameter("ParamAngleY");
        _paramAngleZ = model.FindParameter("ParamAngleZ");
    }

    public static IReadOnlyList<string> Modes => ModeList;

    /// <summary>当前舞蹈模式（淡出中仍保持，完全停下变 null）。</summary>
    public string? ActiveMode => _mode;

    /// <summary>是否还在跳（含淡出）。</summary>
    public bool HasActive => _mode is not null;

    /// <summary>速度倍率（0.5~2.0）。</summary>
    public double Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 0.5, 2.0);
    }

    /// <summary>当前相位（弧度，自检 / UI 可视化用；速度 2x 时推进约 2 倍）。</summary>
    public double Phase => _phase;

    /// <summary>本帧写过的参数个数（运行时下一帧开头复位）。</summary>
    public int TouchedCount => _touchedCount;

    public int GetTouchedIndex(int index) => _touchedIndices[index];

    /// <summary>开始跳舞（未知名返回 false）。</summary>
    public bool Start(string mode, double speed = 1.0)
    {
        if (!IsKnownMode(mode)) return false;
        _mode = mode;
        Speed = speed;
        _stopping = false;
        return true;
    }

    /// <summary>停止跳舞（0.25 秒权重淡出，停止后不再写参数）。</summary>
    public void Stop()
    {
        _stopping = true;
    }

    /// <summary>立即停下（不需要淡出时用）。</summary>
    public void StopImmediate()
    {
        _mode = null;
        _stopping = false;
        _weight = 0;
        _touchedCount = 0;
    }

    /// <summary>推进一帧（在动作更新之后调用，覆盖 IDLE 的手臂曲线；物理求值之前）。</summary>
    public void Update(double deltaSeconds)
    {
        _touchedCount = 0;
        if (_mode is null) return;

        var targetWeight = _stopping ? 0f : 1f;
        var step = (float)(deltaSeconds / 0.25);
        if (_weight < targetWeight)
        {
            _weight = Math.Min(targetWeight, _weight + step);
        }
        else if (_weight > targetWeight)
        {
            _weight = Math.Max(targetWeight, _weight - step);
        }

        if (_stopping && _weight <= 0f)
        {
            _mode = null;
            _stopping = false;
            return;
        }

        var pulse = _speed * deltaSeconds;
        _phase += pulse * Math.PI * 2.0 * BaseFrequency(_mode);
        if (_phase > Math.PI * 2_000_000) _phase -= Math.PI * 2_000_000;

        var w = _weight;
        if (w <= 0f) return;

        switch (_mode)
        {
            case ModeSway:
                ApplySway(w);
                break;
            case ModeWave:
                ApplyWave(w);
                break;
            case ModeSpin:
                ApplySpin(w);
                break;
            case ModeShimmy:
                ApplyShimmy(w);
                break;
        }
    }

    /// <summary>运行时下一帧开头复位「上一帧舞蹈写过的参数」。</summary>
    public void ResetTouched(CubismParameter[] parameters)
    {
        for (var i = 0; i < _touchedCount; i++)
        {
            var parameter = parameters[_touchedIndices[i]];
            parameter.Value = parameter.DefaultValue;
        }

        _touchedCount = 0;
    }

    private static double BaseFrequency(string mode) => mode switch
    {
        ModeSway => 0.85,
        ModeWave => 1.10,
        ModeSpin => 0.65,
        ModeShimmy => 1.55,
        _ => 1.0,
    };

    private void ApplySway(float w)
    {
        var p = _phase;
        var kick = Math.Sin(p);
        var second = Math.Sin(p * 2.0);
        Write(_lArm, 0.30f * kick * w);
        Write(_lForearm, 0.22f * Math.Sin(p + 0.4) * w);
        Write(_lWrist, 0.16f * second * w);
        Write(_lHand, 0.10f * second * w);
        Write(_rArm, -0.30f * kick * w);
        Write(_rForearm, -0.22f * Math.Sin(p + 0.4) * w);
        Write(_rWrist, -0.16f * second * w);
        Write(_rHand, -0.10f * second * w);
        Write(_shoulderY, (float)(2.6 * kick) * w);
        Write(_shoulderX, (float)(1.2 * Math.Sin(p * 2.0 + 0.6)) * w);
        Write(_angleX, (float)(1.8 * Math.Sin(p * 0.5)) * w);
        Write(_angleZ, (float)(2.2 * kick) * w);
        Write(_positionX, (float)(1.8 * kick) * w);
        Write(_paramAngleZ, (float)(1.4 * kick) * w);
        Write(_paramAngleY, (float)(1.2 * second) * w);
    }

    private void ApplyWave(float w)
    {
        var p = _phase;
        var wave = Math.Sin(p * 2.0);
        Write(_rArm, (float)(-0.55 + (0.12 * Math.Sin(p))) * w);
        Write(_rForearm, (float)(0.35 + (0.22 * Math.Sin(p + 0.3))) * w);
        Write(_rWrist, (float)(0.32 * wave) * w);
        Write(_rHand, (float)(0.24 * Math.Sin((p * 2.0) + 0.5)) * w);
        Write(_rHand2, (float)(0.12 * wave) * w);
        Write(_lArm, (float)(0.12 * Math.Sin(p * 0.5)) * w);
        Write(_lForearm, (float)(0.10 * Math.Sin(p * 0.5 + 0.3)) * w);
        Write(_shoulderY, (float)(-2.5 + (1.6 * Math.Sin(p))) * w);
        Write(_shoulderZ, (float)(1.4 * Math.Sin(p + 0.9)) * w);
        Write(_angleX, (float)(1.6 * Math.Sin(p * 0.5)) * w);
        Write(_angleY, (float)(1.2 * Math.Sin(p * 0.75)) * w);
        Write(_positionX, (float)(1.4 * Math.Sin(p * 0.5)) * w);
        Write(_paramAngleZ, (float)(2.2 * Math.Sin(p * 0.5 + 0.4)) * w);
        Write(_paramAngleY, (float)(1.5 * Math.Sin(p * 0.5)) * w);
    }

    private void ApplySpin(float w)
    {
        var p = _phase;
        var swing = Math.Sin(p);
        var swing2 = Math.Sin(p * 2.0);
        Write(_lArm, (float)(-0.42 * swing) * w);
        Write(_lForearm, (float)(0.30 * Math.Sin(p + 0.5)) * w);
        Write(_lWrist, (float)(0.18 * swing2) * w);
        Write(_rArm, (float)(0.42 * swing) * w);
        Write(_rForearm, (float)(-0.30 * Math.Sin(p + 0.5)) * w);
        Write(_rWrist, (float)(-0.18 * swing2) * w);
        Write(_shoulderX, (float)(2.0 * swing2) * w);
        Write(_shoulderY, (float)(2.0 * swing) * w);
        Write(_angleZ, (float)(5.0 * swing) * w);
        Write(_angleY, (float)(2.0 * swing2) * w);
        Write(_positionX, (float)(4.0 * swing) * w);
        Write(_leanInput, (float)(5.0 * swing) * w);
        Write(_paramAngleY, (float)(3.0 * Math.Sin(p + 0.5)) * w);
        Write(_paramAngleZ, (float)(3.0 * Math.Sin(p * 0.5)) * w);
    }

    private void ApplyShimmy(float w)
    {
        var p = _phase;
        var fast = Math.Sin(p * 2.0);
        var shoulder = Math.Sin(p);
        Write(_shoulderX, (float)(3.2 * shoulder) * w);
        Write(_shoulderY, (float)(2.4 * fast) * w);
        Write(_shoulderZ, (float)(1.6 * Math.Sin(p + 1.0)) * w);
        Write(_shoulderYy, (float)(2.2 * Math.Sin(p + 0.7)) * w);
        Write(_lArm, (float)(0.16 * fast) * w);
        Write(_rArm, (float)(-0.16 * fast) * w);
        Write(_lForearm, (float)(0.10 * Math.Sin(p + 0.3)) * w);
        Write(_rForearm, (float)(-0.10 * Math.Sin(p + 0.3)) * w);
        Write(_angleX, (float)(2.0 * fast) * w);
        Write(_positionX, (float)(1.6 * shoulder) * w);
        Write(_paramAngleY, (float)(1.6 * fast) * w);
        Write(_paramAngleZ, (float)(1.0 * shoulder) * w);
    }

    private void Write(CubismParameter? parameter, double value)
    {
        if (parameter is null) return;
        parameter.Value = (float)value;
        if (_touchedCount >= _touchedIndices.Length)
        {
            Array.Resize(ref _touchedIndices, _touchedIndices.Length * 2);
        }

        _touchedIndices[_touchedCount++] = parameter.Index;
    }

    private static bool IsKnownMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return false;
        foreach (var known in ModeList)
        {
            if (string.Equals(known, mode.Trim(), StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
