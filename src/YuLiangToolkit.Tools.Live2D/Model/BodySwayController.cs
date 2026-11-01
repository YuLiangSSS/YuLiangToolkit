using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 身体晃动测试控制器：用一组「身体主轴 / 物理输入 / 腰部 / 腿部 / 头部」参数做正弦晃动，
/// 方便验证物理链（头发 / 尾巴 / 衣服）在身体动起来时是否自然。
///
/// <para><b>参数分工</b>（本模型实测）：<c>ParamBodyAngle*</c> 直接驱动身体变形；
/// <c>PositionX/Y/Z</c> 是物理输入（物理 Setting 4 / 14 / 9 会把它变成 <c>ParamBodyAngle*</c> 并带动腰部 / 腿 / 头发链）；
/// <c>AngleX/Y/Z</c> 是头部角度控制器（物理 Setting 1~3 会变成 <c>ParamAngle*</c>）。
/// 所以这里「身体」两套都写：直接写 <c>ParamBodyAngle*</c>（物理关时也有效）+ 写 <c>Position*</c>（物理开时带动下游）。
/// 头部补偿写 <c>ParamAngle*</c> 和 <c>Angle*</c>（物理开时后者生效）。</para>
///
/// <para><b>性能</b>：每帧只写十几个参数、复用 <see cref="LastApplied"/> 列表，0 堆分配、&lt;0.1ms。</para>
///
/// <para><b>模式字符串</b>与 <c>Live2DConfig.BodySwayMode</c> 的界面下拉一致（见 <see cref="Modes"/>）。</para>
/// </summary>
public sealed class BodySwayController
{
    // ---------- 模式名（UI 下拉直接用） ----------

    /// <summary>左右摇。</summary>
    public const string ModeLeftRight = "左右摇";

    /// <summary>前后摇。</summary>
    public const string ModeForwardBack = "前后摇";

    /// <summary>扭腰。</summary>
    public const string ModeTwist = "扭腰";

    /// <summary>椭圆（X/Z 相位差 90°）。</summary>
    public const string ModeEllipse = "椭圆";

    /// <summary>自然晃（多频叠加）。</summary>
    public const string ModeNatural = "自然晃";

    /// <summary>全部模式名（UI 下拉绑定用）。</summary>
    public static IReadOnlyList<string> Modes { get; } = new[]
    {
        ModeLeftRight, ModeForwardBack, ModeTwist, ModeEllipse, ModeNatural,
    };

    // ---------- 权重 / 相位常量（经验值，按观感调） ----------

    /// <summary>身体主轴晃动权重（相对幅度）。</summary>
    private const float BodyWeight = 1.0f;

    /// <summary>物理输入 Position* 的权重（与身体主轴同步；物理会再把它变回身体角度）。</summary>
    private const float PhysicsInputWeight = 1.0f;

    /// <summary>腰部跟随身体的权重（扭腰 / 椭圆时胯部反向转，看起来更自然）。</summary>
    private const float HipWeight = 0.55f;

    /// <summary>「自然晃」第二频率相对主频的倍率。</summary>
    private const float NaturalSecondaryFrequency = 0.37f;

    /// <summary>「自然晃」第二频率的幅度权重。</summary>
    private const float NaturalSecondaryWeight = 0.25f;

    /// <summary>「自然晃」前后慢速分量的频率倍率。</summary>
    private const float NaturalYFrequency = 0.23f;

    /// <summary>「自然晃」前后慢速分量的幅度权重。</summary>
    private const float NaturalYWeight = 0.18f;

    /// <summary>「自然晃」左右侧倾分量的频率倍率。</summary>
    private const float NaturalZFrequency = 0.61f;

    /// <summary>「自然晃」左右侧倾分量的幅度权重。</summary>
    private const float NaturalZWeight = 0.14f;

    /// <summary>「椭圆」模式 Z 方向幅度权重。</summary>
    private const float EllipseZWeight = 0.6f;

    /// <summary>「左右摇」附带的小幅侧倾权重（让动作不那么像单轴摆动）。</summary>
    private const float LeftRightZWeight = 0.15f;

    /// <summary>头部反向补偿系数（最终再乘 <see cref="HeadFollow"/>）；0.9 让补偿别太猛。</summary>
    private const float HeadCounterWeight = 0.9f;

    /// <summary>头部补偿小于这个角度就不写（避免把鼠标 / 动捕的头部跟随压成 0）。</summary>
    private const float HeadCounterDeadZone = 0.05f;

    // ---------- 参数表（构造时解析一次，之后每帧直接写） ----------

    private static readonly string[] ManagedIds =
    {
        "ParamBodyAngleX", "ParamBodyAngleY", "ParamBodyAngleZ",
        "PositionX", "PositionY", "PositionZ",
        "AngleX", "AngleY", "AngleZ",
        "ParamAngleX", "ParamAngleY", "ParamAngleZ",
        "HipAngleX", "HipAngleY", "HipAngleZ", "ParamHipAngleZ", "LEGAngleZ2",
    };

    private readonly CubismParameter? _paramBodyAngleX;
    private readonly CubismParameter? _paramBodyAngleY;
    private readonly CubismParameter? _paramBodyAngleZ;
    private readonly CubismParameter? _positionX;
    private readonly CubismParameter? _positionY;
    private readonly CubismParameter? _positionZ;
    private readonly CubismParameter? _angleX;
    private readonly CubismParameter? _angleY;
    private readonly CubismParameter? _angleZ;
    private readonly CubismParameter? _paramAngleX;
    private readonly CubismParameter? _paramAngleY;
    private readonly CubismParameter? _paramAngleZ;
    private readonly CubismParameter? _hipAngleX;
    private readonly CubismParameter? _hipAngleY;
    private readonly CubismParameter? _hipAngleZ;
    private readonly CubismParameter? _paramHipAngleZ;
    private readonly CubismParameter? _legAngleZ2;

    private readonly List<(string Id, float Value)> _lastApplied = new(24);

    private double _time;
    private float _phase;

    public BodySwayController(CubismModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _paramBodyAngleX = model.FindParameter("ParamBodyAngleX");
        _paramBodyAngleY = model.FindParameter("ParamBodyAngleY");
        _paramBodyAngleZ = model.FindParameter("ParamBodyAngleZ");
        _positionX = model.FindParameter("PositionX");
        _positionY = model.FindParameter("PositionY");
        _positionZ = model.FindParameter("PositionZ");
        _angleX = model.FindParameter("AngleX");
        _angleY = model.FindParameter("AngleY");
        _angleZ = model.FindParameter("AngleZ");
        _paramAngleX = model.FindParameter("ParamAngleX");
        _paramAngleY = model.FindParameter("ParamAngleY");
        _paramAngleZ = model.FindParameter("ParamAngleZ");
        _hipAngleX = model.FindParameter("HipAngleX");
        _hipAngleY = model.FindParameter("HipAngleY");
        _hipAngleZ = model.FindParameter("HipAngleZ");
        _paramHipAngleZ = model.FindParameter("ParamHipAngleZ");
        _legAngleZ2 = model.FindParameter("LEGAngleZ2");
    }

    // ---------- 输入（ModelRuntime 每帧从自身属性同步） ----------

    /// <summary>晃动模式（<see cref="Modes"/> 之一；未知模式按左右摇处理）。</summary>
    public string Mode { get; set; } = ModeLeftRight;

    /// <summary>晃动幅度（度，0~30）。</summary>
    public double AmplitudeDegrees { get; set; } = 12.0;

    /// <summary>晃动速度（Hz，0.05~2）。</summary>
    public double SpeedHz { get; set; } = 0.5;

    /// <summary>头部反向补偿比例（0 = 头完全不动，1 = 完全反向抵消身体倾斜）。</summary>
    public double HeadFollow { get; set; } = 0.35;

    /// <summary>是否连腰部 / 腿部参数一起晃。</summary>
    public bool IncludeHips { get; set; } = true;

    // ---------- 输出（UI 显示用） ----------

    /// <summary>当前相位（0~1；UI 画正弦曲线 / 进度用）。</summary>
    public float Phase => _phase;

    /// <summary>本帧实际写入的参数（Id + 写入值；复用列表，每帧重建，0 分配）。</summary>
    public IReadOnlyList<(string Id, float Value)> LastApplied => _lastApplied;

    /// <summary>这个控制器可能写到的参数 Id（ModelRuntime 收集受管参数用）。</summary>
    public static IReadOnlyList<string> ManagedParameterIds => ManagedIds;

    /// <summary>复位时间 / 相位 / 记录（开始新一段晃动测试时调）。</summary>
    public void Reset()
    {
        _time = 0;
        _phase = 0;
        _lastApplied.Clear();
    }

    /// <summary>停用晃动时清掉「本帧写了哪些参数」的记录（UI 显示「没有在晃」）。</summary>
    public void ClearApplied() => _lastApplied.Clear();

    /// <summary>推进一帧并把晃动量写进模型参数（幅度单位：度；超出参数范围由参数自身 clamp）。</summary>
    public void Apply(CubismModel model, double deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (deltaSeconds <= 0) return;

        _lastApplied.Clear();

        var amplitude = (float)Math.Clamp(AmplitudeDegrees, 0.0, 30.0);
        var speed = Math.Clamp(SpeedHz, 0.05, 2.0);
        _time += deltaSeconds;
        var progress = _time * speed;
        _phase = (float)(progress - Math.Floor(progress));
        var angle = progress * Math.PI * 2.0;

        // 先算三个身体轴的目标角度（单位：度）
        float bodyX = 0;
        float bodyY = 0;
        float bodyZ = 0;

        switch (Mode)
        {
            case ModeForwardBack:
            {
                bodyY = amplitude * (float)Math.Sin(angle);
                break;
            }

            case ModeTwist:
            {
                bodyZ = amplitude * (float)Math.Sin(angle);
                // 扭腰时身体带一点点左右倾，看起来像真的在扭
                bodyX = amplitude * 0.12f * (float)Math.Sin(angle + (Math.PI * 0.5));
                break;
            }

            case ModeEllipse:
            {
                bodyX = amplitude * (float)Math.Sin(angle);
                bodyZ = amplitude * EllipseZWeight * (float)Math.Cos(angle);
                break;
            }

            case ModeNatural:
            {
                var main = (float)Math.Sin(angle);
                var secondary = (float)Math.Sin((angle * NaturalSecondaryFrequency) + 0.7);
                bodyX = amplitude * (0.65f * main) + (amplitude * NaturalSecondaryWeight * secondary);
                bodyY = amplitude * NaturalYWeight * (float)Math.Sin((angle * NaturalYFrequency) + 1.3);
                bodyZ = amplitude * NaturalZWeight * (float)Math.Sin((angle * NaturalZFrequency) + 2.1);
                break;
            }

            case ModeLeftRight:
            default:
            {
                bodyX = amplitude * (float)Math.Sin(angle);
                // 左右摇时腿部 / 胯部会有轻微侧倾
                bodyZ = amplitude * LeftRightZWeight * (float)Math.Sin(angle + (Math.PI * 0.5));
                break;
            }
        }

        bodyX *= BodyWeight;
        bodyY *= BodyWeight;
        bodyZ *= BodyWeight;

        // 1) 身体主轴：直接参数 + 物理输入参数（物理开会把它们再变成 ParamBodyAngle* 并带动下游链）
        Write("ParamBodyAngleX", _paramBodyAngleX, bodyX);
        Write("ParamBodyAngleY", _paramBodyAngleY, bodyY);
        Write("ParamBodyAngleZ", _paramBodyAngleZ, bodyZ);
        Write("PositionX", _positionX, bodyX * PhysicsInputWeight);
        Write("PositionY", _positionY, bodyY * PhysicsInputWeight);
        Write("PositionZ", _positionZ, bodyZ * PhysicsInputWeight);

        // 2) 头部反向补偿：身体往左斜，头往右回一点，让镜头里看着稳
        var headFollow = (float)Math.Clamp(HeadFollow, 0.0, 1.0) * HeadCounterWeight;
        var headX = -bodyX * headFollow;
        var headY = -bodyY * headFollow;
        var headZ = -bodyZ * headFollow;
        if (MathF.Abs(headX) > HeadCounterDeadZone || MathF.Abs(headY) > HeadCounterDeadZone ||
            MathF.Abs(headZ) > HeadCounterDeadZone)
        {
            Write("ParamAngleX", _paramAngleX, headX);
            Write("ParamAngleY", _paramAngleY, headY);
            Write("ParamAngleZ", _paramAngleZ, headZ);
            Write("AngleX", _angleX, headX);
            Write("AngleY", _angleY, headY);
            Write("AngleZ", _angleZ, headZ);
        }

        // 3) 腰部 / 腿部：物理在跑时由 Position* 链带动；这里直写一份，物理关掉时也有晃动
        if (!IncludeHips) return;

        var hipX = bodyX * HipWeight;
        var hipY = bodyY * HipWeight;
        var hipZ = -bodyZ * HipWeight;
        Write("HipAngleX", _hipAngleX, hipX);
        Write("HipAngleY", _hipAngleY, hipY);
        Write("HipAngleZ", _hipAngleZ, hipZ);
        Write("ParamHipAngleZ", _paramHipAngleZ, hipZ);
        Write("LEGAngleZ2", _legAngleZ2, hipZ * 0.6f);
    }

    private void Write(string id, CubismParameter? parameter, float value)
    {
        if (parameter is null) return;
        parameter.Value = value;
        _lastApplied.Add((id, parameter.Value));
    }
}
