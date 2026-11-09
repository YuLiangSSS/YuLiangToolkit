namespace YuLiangToolkit.Tools.Live2D.Play;

/// <summary>
/// 摇尾巴：模型尾巴由物理（PhysicsSetting130）驱动 `Param_Angle_Rotation84~89`，
/// 这里在物理求值之后给 6 段尾巴叠加一段带相位延迟的正弦摆动——根部小幅、尾尖大幅，
/// 像真的尾巴那样一节带一节甩。
///
/// <para>纯函数（自检直接断言）：<see cref="Envelope"/> 两端淡入淡出、<see cref="Offset"/> 相位滞后。</para>
/// </summary>
public static class TailWag
{
    /// <summary>摆动频率（Hz）：约 1.35 次/秒，轻快但不抽风。</summary>
    public const double FrequencyHz = 1.35;

    /// <summary>首尾各用多少秒淡入 / 淡出（避免忽然开始甩、忽然定住）。</summary>
    public const double FadeSeconds = 0.3;

    /// <summary>尾巴段数（84~89）。</summary>
    public const int SegmentCount = 6;

    /// <summary>每段相对根部的相位延迟（弧度）：越靠尾尖越慢半拍。</summary>
    public const double PhaseLagPerSegment = 0.55;

    /// <summary>高好感门槛（和互动档位一致：≥85 才摇）。</summary>
    public const int HighAffectionThreshold = 85;

    /// <summary>每次触摸触发摇尾巴的概率（百分比）。</summary>
    public const int TriggerChancePercent = 35;

    /// <summary>触发判断（纯函数，自检用）：尾巴可见 + 高好感 + 随机数落在概率内。</summary>
    public static bool ShouldWag(double affection, bool tailVisible, int rollPercent)
        => tailVisible && affection >= HighAffectionThreshold && rollPercent >= 0 && rollPercent < TriggerChancePercent;

    /// <summary>摆动包络：两端 <see cref="FadeSeconds"/> 秒淡入淡出，中间为 1；动作外为 0。</summary>
    public static double Envelope(double elapsedSeconds, double durationSeconds)
    {
        if (durationSeconds <= 0 || elapsedSeconds <= 0 || elapsedSeconds >= durationSeconds) return 0;
        var fade = Math.Min(1.0, Math.Min(elapsedSeconds / FadeSeconds, (durationSeconds - elapsedSeconds) / FadeSeconds));
        return Math.Clamp(fade, 0, 1);
    }

    /// <summary>第几段尾巴的幅度系数：根部 0.55、尾尖约 1.10。</summary>
    public static double AmplitudeScale(int segmentIndex)
        => 0.55 + (Math.Clamp(segmentIndex, 0, SegmentCount - 1) * 0.11);

    /// <summary>
    /// 某段尾巴在某时刻的角度偏移（度）：<c>sin(2πft - 段号×相位延迟) × 幅度 × 分段系数 × 包络</c>。
    /// </summary>
    public static double Offset(double elapsedSeconds, double durationSeconds, double amplitudeDegrees, int segmentIndex)
    {
        var envelope = Envelope(elapsedSeconds, durationSeconds);
        if (envelope <= 0 || amplitudeDegrees == 0) return 0;
        var phase = (2 * Math.PI * FrequencyHz * elapsedSeconds) - (segmentIndex * PhaseLagPerSegment);
        return Math.Sin(phase) * amplitudeDegrees * AmplitudeScale(segmentIndex) * envelope;
    }
}
