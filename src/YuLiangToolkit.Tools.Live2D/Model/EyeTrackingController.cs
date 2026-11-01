using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 眼睛 / 头部 / 身体跟随：目标画布像素坐标 → 归一化朝向 → 平滑 → 写参数。
///
/// <para><b>这个模型的两套头部参数</b>（实测确认）：<c>AngleX/Y/Z</c>（cdi3 里叫 OX/OY/OZ，控制器）
/// 本身不驱动任何网格；物理里的 PhysicsSetting1~3 把 <c>AngleX</c> 变成 <c>ParamAngleX</c>。
/// 所以这里两套都写：物理开着时 <c>ParamAngle*</c> 会被物理按 <c>Angle*</c> 重算（更平滑），
/// 物理关掉时直接写 <c>ParamAngle*</c> 也能动。</para>
///
/// <para>同理身体：<c>PositionX/Y</c> 经物理变成 <c>ParamBodyAngleX/Y</c>，所以两套都写。</para>
/// </summary>
internal sealed class EyeTrackingController
{
    /// <summary>头部最大转角（度，与 ParamAngleX/Y 的范围一致）。</summary>
    private const float MaxHeadAngle = 30.0f;

    /// <summary>头部最大侧倾（度）。</summary>
    private const float MaxHeadRoll = 8.0f;

    /// <summary>身体跟随头部转角的比例（再乘 BodyStrength）。</summary>
    private const float BodyFollowRatio = 0.3f;

    private readonly float _centerX;
    private readonly float _centerY;

    private readonly CubismParameter? _paramAngleX;
    private readonly CubismParameter? _paramAngleY;
    private readonly CubismParameter? _paramAngleZ;
    private readonly CubismParameter? _angleX;
    private readonly CubismParameter? _angleY;
    private readonly CubismParameter? _angleZ;
    private readonly CubismParameter? _paramEyeBallX;
    private readonly CubismParameter? _paramEyeBallY;
    private readonly CubismParameter? _paramBodyAngleX;
    private readonly CubismParameter? _paramBodyAngleY;
    private readonly CubismParameter? _paramBodyAngleZ;
    private readonly CubismParameter? _positionX;
    private readonly CubismParameter? _positionY;
    private readonly CubismParameter? _positionZ;

    private bool _hasTarget;
    private float _targetCanvasX;
    private float _targetCanvasY;
    private float _currentX;
    private float _currentY;

    public EyeTrackingController(CubismModel model)
    {
        _centerX = model.CanvasOriginX > 0 ? model.CanvasOriginX : model.CanvasWidthPixels * 0.5f;
        _centerY = model.CanvasOriginY > 0 ? model.CanvasOriginY : model.CanvasHeightPixels * 0.5f;
        if (_centerX <= 0) _centerX = 1.0f;
        if (_centerY <= 0) _centerY = 1.0f;

        _paramAngleX = model.FindParameter("ParamAngleX");
        _paramAngleY = model.FindParameter("ParamAngleY");
        _paramAngleZ = model.FindParameter("ParamAngleZ");
        _angleX = model.FindParameter("AngleX");
        _angleY = model.FindParameter("AngleY");
        _angleZ = model.FindParameter("AngleZ");
        _paramEyeBallX = model.FindParameter("ParamEyeBallX");
        _paramEyeBallY = model.FindParameter("ParamEyeBallY");
        _paramBodyAngleX = model.FindParameter("ParamBodyAngleX");
        _paramBodyAngleY = model.FindParameter("ParamBodyAngleY");
        _paramBodyAngleZ = model.FindParameter("ParamBodyAngleZ");
        _positionX = model.FindParameter("PositionX");
        _positionY = model.FindParameter("PositionY");
        _positionZ = model.FindParameter("PositionZ");
    }

    /// <summary>平滑系数（0 = 立即到目标；越大越慢，按 60fps 归一化）。</summary>
    public double Smoothing { get; set; } = 0.35;

    /// <summary>头部跟随强度（0~1.5；实际角度仍会被 ParamAngle* 的 ±30° 参数范围截断）。</summary>
    public double HeadStrength { get; set; } = 1.0;

    /// <summary>眼珠跟随强度（0~1.5；ParamEyeBall* 本身是 -1~1）。</summary>
    public double EyeStrength { get; set; } = 1.0;

    /// <summary>身体跟随强度（0~1.5）。</summary>
    public double BodyStrength { get; set; } = 0.5;

    /// <summary>横向灵敏度倍率（0.3~3.0）：鼠标离中心越近就能让头 / 眼转到更大角度。</summary>
    public double HorizontalGain { get; set; } = 1.0;

    /// <summary>抬头（目标在中心上方）方向的灵敏度倍率（0.3~3.0）。</summary>
    public double UpGain { get; set; } = 1.0;

    /// <summary>低头（目标在中心下方）方向的灵敏度倍率（0.3~3.0）。</summary>
    public double DownGain { get; set; } = 1.0;

    /// <summary>纵向中性偏移（-0.5~0.5）：先加到纵向归一化目标上，再做正负增益（校正上下不对称）。</summary>
    public double VerticalOffset { get; set; }

    /// <summary>当前是否有跟随目标。</summary>
    public bool HasTarget => _hasTarget;

    /// <summary>平滑后的归一化横向朝向（-1~1，主要给自检 / 调试用）。</summary>
    public float SmoothedX => _currentX;

    /// <summary>平滑后的归一化纵向朝向（-1~1；-1 = 抬头方向）。</summary>
    public float SmoothedY => _currentY;

    /// <summary>设置跟随目标（画布像素坐标，原点画布左上角）。</summary>
    public void SetTarget(float canvasX, float canvasY)
    {
        _hasTarget = true;
        _targetCanvasX = canvasX;
        _targetCanvasY = canvasY;
    }

    /// <summary>清除目标（平滑回到正前方）。</summary>
    public void ClearTarget() => _hasTarget = false;

    /// <summary>
    /// 推进一帧并写参数。<paramref name="trackingEnabled"/> = false 时：
    /// 只写待机摇摆用的 Z 轴参数（头 / 身体轻微晃动），<b>不伸手写</b> ParamAngleX/Y、EyeBall、BodyAngleX/Y，
    /// 这样用户在「面部测试」里手动调的参数不会被鼠标眼控每帧覆盖。
    /// <paramref name="swayAngleZ"/> / <paramref name="swayBodyAngleZ"/> 是待机摇摆量（度）。
    /// </summary>
    public void Update(double deltaSeconds, bool trackingEnabled, float swayAngleZ, float swayBodyAngleZ)
    {
        var targetX = 0.0f;
        var targetY = 0.0f;
        if (trackingEnabled && _hasTarget)
        {
            // 横向：归一化后乘灵敏度
            var rawX = (_targetCanvasX - _centerX) / _centerX;
            var horizontalGain = (float)Math.Clamp(HorizontalGain, 0.3, 3.0);
            targetX = Math.Clamp(rawX * horizontalGain, -1.0f, 1.0f);

            // 纵向：先加中性偏移，再按抬头 / 低头分别乘增益（向上跟不到就调大 DownGain 之类）
            var rawY = ((_targetCanvasY - _centerY) / _centerY) +
                       (float)Math.Clamp(VerticalOffset, -0.5, 0.5);
            var verticalGain = rawY < 0
                ? (float)Math.Clamp(UpGain, 0.3, 3.0)
                : (float)Math.Clamp(DownGain, 0.3, 3.0);
            targetY = Math.Clamp(rawY * verticalGain, -1.0f, 1.0f);
        }

        // 指数平滑：retention 越大越慢；按 60fps 归一化，帧率变化不影响手感
        var retention = Math.Clamp(Smoothing, 0.0, 0.98);
        var factor = 1.0 - Math.Pow(retention, Math.Max(deltaSeconds, 0.0) * 60.0);
        if (factor > 1.0) factor = 1.0;
        _currentX += (targetX - _currentX) * (float)factor;
        _currentY += (targetY - _currentY) * (float)factor;

        if (!trackingEnabled)
        {
            // 鼠标眼控关掉：只留「轻微摇摆」的 Z 轴，X / Y / 眼珠都不碰（让手动调的值保留）
            Set(_paramAngleZ, swayAngleZ);
            Set(_angleZ, swayAngleZ);
            Set(_paramBodyAngleZ, swayBodyAngleZ);
            Set(_positionZ, swayBodyAngleZ);
            return;
        }

        // 强度允许放大到 1.5（角度仍会被 ParamAngle* 的 ±30° 参数范围截断，这是正常的）
        var head = (float)Math.Clamp(HeadStrength, 0.0, 1.5);
        var eye = (float)Math.Clamp(EyeStrength, 0.0, 1.5);
        var body = (float)Math.Clamp(BodyStrength, 0.0, 1.5);

        // 目标在右边（_currentX > 0）→ 头往右转、眼珠往右看；画布 y 向下 → 目标在上（_currentY < 0）→ 抬头
        var angleX = _currentX * MaxHeadAngle * head;
        var angleY = -_currentY * MaxHeadAngle * head;
        var angleZ = (-_currentX * MaxHeadRoll * head) + swayAngleZ;

        var eyeBallX = Math.Clamp(_currentX * eye, -1.0f, 1.0f);
        var eyeBallY = Math.Clamp(-_currentY * eye, -1.0f, 1.0f);

        var bodyAngleX = angleX * body * BodyFollowRatio;
        var bodyAngleY = angleY * body * BodyFollowRatio;

        // 头部（两套参数都写，见类注释）
        Set(_paramAngleX, angleX);
        Set(_paramAngleY, angleY);
        Set(_paramAngleZ, angleZ);
        Set(_angleX, angleX);
        Set(_angleY, angleY);
        Set(_angleZ, angleZ);

        // 眼珠
        Set(_paramEyeBallX, eyeBallX);
        Set(_paramEyeBallY, eyeBallY);

        // 身体：角度 + 物理输入位置参数
        Set(_paramBodyAngleX, bodyAngleX);
        Set(_positionX, bodyAngleX);
        Set(_paramBodyAngleY, bodyAngleY);
        Set(_positionY, bodyAngleY);
        Set(_paramBodyAngleZ, swayBodyAngleZ);
        Set(_positionZ, swayBodyAngleZ);
    }

    private static void Set(CubismParameter? parameter, float value)
    {
        if (parameter is not null) parameter.Value = value;
    }
}
