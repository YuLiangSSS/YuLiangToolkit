namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>播放器进度条左边那个时间怎么显示。</summary>
public enum TimeDisplayMode
{
    /// <summary>正向计时：从 <c>0:00</c> 数到总时长（默认，老行为）。</summary>
    Elapsed = 0,

    /// <summary>剩余倒计时：从总时长倒数到 <c>0:00</c>（即「还差多少秒播完」）。</summary>
    Remaining = 1,
}

/// <summary>
/// 时间显示的**纯逻辑**（自检直接断言）：给「当前位置 + 总时长 + 模式」算出左侧那串时间。
///
/// <para>右侧的总时长**不跟着模式变** —— 倒计时模式下左边是「还剩多少」，右边仍然是「一共多长」，
/// 两个信息都在。时长还没读到（刚打开 / 没加载）时倒计时返回 <c>--:--</c>，免得先假装有 0 秒。</para>
/// </summary>
public static class MediaTimeDisplay
{
    /// <summary>左侧时间文本。</summary>
    public static string Format(TimeSpan position, TimeSpan duration, TimeDisplayMode mode)
    {
        if (mode != TimeDisplayMode.Remaining) return MediaTime.Format(position);

        if (duration <= TimeSpan.Zero) return "--:--";

        // 夹在 0 ~ duration：位置比总时长还大（播放器偶发）时显示 0:00，位置为负时别超过总时长
        var remaining = duration - position;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        if (remaining > duration) remaining = duration;
        return MediaTime.Format(remaining);
    }

    /// <summary>给人看的模式名（设置页提示 / 日志用）。</summary>
    public static string Describe(TimeDisplayMode mode) => mode == TimeDisplayMode.Remaining
        ? "剩余时间（总时长 → 0:00）"
        : "已播放时间（0:00 → 总时长）";
}
