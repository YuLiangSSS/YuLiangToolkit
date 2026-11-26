using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 舞蹈控制器（程序化循环，不依赖动作文件）：4 种模式。
///
/// <para><b>2026-11-27 全套重编</b>：旧版以手臂 / 肩膀参数为主（画面上几乎看不出变化，用户反馈「没动作」）；
/// 新版全部以**身体主轴 + 腰 + 位移**为主力（实测有效、画面上看得见的大变化），头部 / 眼睛 / 嘴巴跟上，
/// 只用参数有效性实测确认过的参数（ShoulderX / ShoulderYy / HipAngleZ / 翅膀等无效参数一律不碰）。
/// 停止时 0.25 秒整体权重淡出回默认（位姿合成层交回）；相位连续推进，速度 0.5~2.0 可调。</para>
/// </summary>
internal sealed class DanceController
{
    public const string ModeSway = "左右摇摆";
    public const string ModeTurn = "转身律动";
    public const string ModeBounce = "蹦跳律动";
    public const string ModeTwist = "扭腰摆胯";

    private static readonly string[] ModeList = { ModeSway, ModeTurn, ModeBounce, ModeTwist };

    private readonly CubismParameter? _bodyAngleX;
    private readonly CubismParameter? _bodyAngleY;
    private readonly CubismParameter? _bodyAngleZ;
    private readonly CubismParameter? _positionX;
    private readonly CubismParameter? _positionY;
    private readonly CubismParameter? _positionZ;
    private readonly CubismParameter? _hipAngleX;
    private readonly CubismParameter? _angleX;
    private readonly CubismParameter? _angleY;
    private readonly CubismParameter? _angleZ;
    private readonly CubismParameter? _paramAngleX;
    private readonly CubismParameter? _paramAngleY;
    private readonly CubismParameter? _paramAngleZ;
    private readonly CubismParameter? _paramAngleZLeg;
    private readonly CubismParameter? _shoulderY;
    private readonly CubismParameter? _mouthOpen;
    private readonly CubismParameter? _jawOpen;

    private string? _mode;
    private double _speed = 1.0;
    private double _phase;
    private float _weight;
    private bool _stopping;

    public DanceController(CubismModel model)
    {
        _bodyAngleX = model.FindParameter("ParamBodyAngleX");
        _bodyAngleY = model.FindParameter("ParamBodyAngleY");
        _bodyAngleZ = model.FindParameter("ParamBodyAngleZ");
        _positionX = model.FindParameter("PositionX");
        _positionY = model.FindParameter("PositionY");
        _positionZ = model.FindParameter("PositionZ");
        _hipAngleX = model.FindParameter("HipAngleX");
        _angleX = model.FindParameter("AngleX");
        _angleY = model.FindParameter("AngleY");
        _angleZ = model.FindParameter("AngleZ");
        _paramAngleX = model.FindParameter("ParamAngleX");
        _paramAngleY = model.FindParameter("ParamAngleY");
        _paramAngleZ = model.FindParameter("ParamAngleZ");
        _paramAngleZLeg = model.FindParameter("LEGAngleZ2");
        _shoulderY = model.FindParameter("ShoulderY");
        _mouthOpen = model.FindParameter("ParamMouthOpenY");
        _jawOpen = model.FindParameter("JawOpen");
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
    }

    /// <summary>推进一帧（在位姿合成期、本层的捕获里调用；物理求值之前）。</summary>
    public void Update(double deltaSeconds)
    {
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
            case ModeTurn:
                ApplyTurn(w);
                break;
            case ModeBounce:
                ApplyBounce(w);
                break;
            case ModeTwist:
                ApplyTwist(w);
                break;
        }
    }

    private static double BaseFrequency(string mode) => mode switch
    {
        ModeSway => 0.9,
        ModeTurn => 0.6,
        ModeBounce => 1.2,
        ModeTwist => 1.0,
        _ => 1.0,
    };

    /// <summary>左右摇摆：身体大幅左右 + 腰跟随 + 位移 + 头反向稳定。</summary>
    private void ApplySway(float w)
    {
        var p = _phase;
        var swing = Math.Sin(p);
        var second = Math.Sin(p * 2.0);
        Write(_bodyAngleX, 8f * swing * w);
        Write(_positionX, 3.2f * swing * w);
        Write(_hipAngleX, 4.5f * swing * w);
        Write(_bodyAngleZ, 2.5f * Math.Sin(p + 0.5) * w);
        Write(_angleZ, 3.5f * swing * w);
        Write(_paramAngleZ, 3f * swing * w);
        Write(_angleY, 1.5f * second * w);
        Write(_paramAngleY, 1.8f * second * w);
        Write(_shoulderY, 2.2f * Math.Sin(p + 1.1) * w);
        Write(_paramAngleZLeg, 2.5f * swing * w);
    }

    /// <summary>转身律动：身体左右转 + 头部跟着 / 反抗 + 眼神扫视。</summary>
    private void ApplyTurn(float w)
    {
        var p = _phase;
        var swing = Math.Sin(p);
        var swingHalf = Math.Sin(p * 0.5f);
        Write(_bodyAngleZ, 8.5f * swing * w);
        Write(_positionX, 2.8f * swing * w);
        Write(_angleZ, 5.5f * swing * w);
        Write(_paramAngleZ, 4.5f * swing * w);
        Write(_angleY, 3.5f * swingHalf * w);
        Write(_paramAngleY, 3f * swingHalf * w);
        Write(_angleX, 1.5f * Math.Sin(p * 2.0) * w);
        Write(_bodyAngleX, 2.5f * Math.Sin(p + 0.9) * w);
    }

    /// <summary>蹦跳律动：前后弹跳感 + 张嘴节奏 + 点头，像跟着歌蹦。</summary>
    private void ApplyBounce(float w)
    {
        var p = _phase;
        var bounce = Math.Abs(Math.Sin(p));
        var beat = Math.Sin(p * 2.0);
        Write(_bodyAngleY, 6f * bounce * w);
        Write(_positionY, 2.2f * bounce * w);
        Write(_mouthOpen, (0.30f + (0.25f * beat)) * w);
        Write(_jawOpen, (0.20f + (0.15f * beat)) * w);
        Write(_paramAngleY, 2.5f * beat * w);
        Write(_angleY, 2f * beat * w);
        Write(_bodyAngleX, 3f * Math.Sin(p * 0.5) * w);
        Write(_positionX, 1.5f * Math.Sin(p * 0.5) * w);
        Write(_shoulderY, 2.5f * bounce * w);
    }

    /// <summary>扭腰摆胯：腰大幅扭 + 身体反方向平衡 + 腿部跟随。</summary>
    private void ApplyTwist(float w)
    {
        var p = _phase;
        var swing = Math.Sin(p);
        var swing2 = Math.Sin(p * 2.0);
        Write(_hipAngleX, 5.5f * swing * w);
        Write(_bodyAngleZ, 8f * swing * w);
        Write(_bodyAngleX, 3f * swing2 * w);
        Write(_positionX, 2.5f * swing * w);
        Write(_angleZ, 4.5f * -swing * w);
        Write(_paramAngleZ, 4f * -swing * w);
        Write(_paramAngleZLeg, 3f * swing * w);
        Write(_angleX, 1.8f * swing2 * w);
        Write(_paramAngleY, 1.6f * swing2 * w);
    }

    private void Write(CubismParameter? parameter, double value)
    {
        if (parameter is null) return;
        parameter.Value = (float)value;
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
