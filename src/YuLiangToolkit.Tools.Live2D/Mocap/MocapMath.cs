namespace YuLiangToolkit.Tools.Live2D.Mocap;

/// <summary>
/// 动捕的纯数学映射（无摄像头依赖，方便自检断言）：
/// 人脸在画面里的归一化位置 → 头部角度 / 眼球方向。
///
/// <para>思路：把「脸中心相对中性位置的偏移」按画面尺寸归一化，再乘一个「偏移多少算满角度」的窗口。
/// 左右偏移 ±0.20（画面宽 20%）≈ 满 ±30°；上下偏移 ±0.16 ≈ 满 ±22°（人脸上下移动通常比左右小）。
/// 这些常量是经验值，界面上用「幅度」滑杆再缩放。</para>
/// </summary>
public static class MocapMath
{
    /// <summary>左右偏移多少（占画面宽比例）算满幅度 ±30°。</summary>
    public const double YawWindow = 0.20;

    /// <summary>上下偏移多少（占画面高比例）算满幅度 ±22°。</summary>
    public const double PitchWindow = 0.16;

    /// <summary>头部左右最大角度（度，与模型 ParamAngleX 的 min/max 一致）。</summary>
    public const double MaxYawDegrees = 30.0;

    /// <summary>头部上下最大角度（度，与模型 ParamAngleY 的 min/max 一致）。</summary>
    public const double MaxPitchDegrees = 22.0;

    /// <summary>脸中心（归一化）→ 头部左右 / 上下角度（度，正 = 右 / 抬头）。</summary>
    public static (double Yaw, double Pitch) MapHead(double faceX, double faceY, double neutralFaceX, double neutralFaceY)
    {
        var yaw = (faceX - neutralFaceX) / YawWindow * MaxYawDegrees;
        // 画面 y 向下：脸往下走 = 低头 = 负角度
        var pitch = (neutralFaceY - faceY) / PitchWindow * MaxPitchDegrees;
        return (Math.Clamp(yaw, -MaxYawDegrees, MaxYawDegrees), Math.Clamp(pitch, -MaxPitchDegrees, MaxPitchDegrees));
    }

    /// <summary>头部角度 → 眼球方向（-1~1；眼睛大致跟着头转，但幅度更小）。</summary>
    public static (double PupilX, double PupilY) MapPupil(double yawDegrees, double pitchDegrees)
    {
        var x = Math.Clamp(yawDegrees / MaxYawDegrees * 0.9, -1, 1);
        var y = Math.Clamp(pitchDegrees / MaxPitchDegrees * 0.6, -1, 1);
        return (x, y);
    }

    /// <summary>指数平滑一步（current 向 target 靠近；smoothing 0~0.95，越大越稳）。</summary>
    public static double Smooth(double current, double target, double smoothing)
    {
        var alpha = 1.0 - Math.Clamp(smoothing, 0, 0.95);
        return current + (target - current) * alpha;
    }
}
