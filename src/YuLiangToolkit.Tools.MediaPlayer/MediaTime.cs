namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>
/// 时间与「快进 / 快退」的算术（**纯函数**，自检直接断言）。
///
/// <para>界面上的位置文本、进度条比例、以及快进快退的夹取全走这里，
/// 免得每个调用点各写一份 <c>Math.Clamp</c> 和 <c>ToString("m\\:ss")</c>。</para>
/// </summary>
public static class MediaTime
{
    /// <summary>快进 / 快退步长的允许范围（秒）。</summary>
    public const double MinSeekStepSeconds = 0.1;

    /// <summary>快进 / 快退步长的上限（秒）：一分钟，再大就没有实用意义了（设置页的滑块也是这个范围）。</summary>
    public const double MaxSeekStepSeconds = 60;

    /// <summary>把步长规整成「0.1 秒精度 + 落在允许范围内」（设置项用的是这个值）。</summary>
    public static double RoundStep(double seconds)
    {
        if (double.IsNaN(seconds)) return MinSeekStepSeconds;
        return Math.Round(Math.Clamp(seconds, MinSeekStepSeconds, MaxSeekStepSeconds), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>时长文本：<c>0:00</c> / <c>3:05</c> / <c>1:02:03</c>；负数与非法值按 <c>0:00</c>。</summary>
    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var totalHours = (int)(value.TotalHours);
        return totalHours > 0
            ? $"{totalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    /// <summary>秒数 → 时长文本（<c>MediaElement</c> 的时长是 <see cref="TimeSpan"/>，配置里存的是秒）。</summary>
    public static string FormatSeconds(double seconds) => Format(TimeSpan.FromSeconds(double.IsNaN(seconds) ? 0 : seconds));

    /// <summary>把位置夹到「0 ~ 时长」里；**时长未知时（<see cref="TimeSpan.Zero"/>）只夹下界**。</summary>
    public static TimeSpan Clamp(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration) return duration;
        return position;
    }

    /// <summary>快进（正数）/ 快退（负数）一步：夹到 0 ~ 时长，返回值可以立刻交给播放器。</summary>
    public static TimeSpan Seek(TimeSpan position, TimeSpan duration, double deltaSeconds)
    {
        if (double.IsNaN(deltaSeconds)) return Clamp(position, duration);
        return Clamp(position + TimeSpan.FromSeconds(deltaSeconds), duration);
    }

    /// <summary>位置 → 进度 0.0 ~ 1.0（时长未知时返回 0，进度条不会乱跳）。</summary>
    public static double Ratio(TimeSpan position, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return 0;
        var ratio = position.TotalMilliseconds / duration.TotalMilliseconds;
        return double.IsNaN(ratio) ? 0 : Math.Clamp(ratio, 0, 1);
    }

    /// <summary>进度 0.0 ~ 1.0 → 位置（点进度条 / 拖动时用；时长未知时返回 0）。</summary>
    public static TimeSpan FromRatio(double ratio, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return TimeSpan.Zero;
        if (double.IsNaN(ratio)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(duration.TotalMilliseconds * Math.Clamp(ratio, 0, 1));
    }
}
