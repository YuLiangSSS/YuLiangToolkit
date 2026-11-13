using System.Globalization;

namespace YuLiangToolkit.Tools.Countdown;

/// <summary>倒计时当前状态。</summary>
public enum CountdownState
{
    /// <summary>停着（还没开始，或已重置）。</summary>
    Idle,

    /// <summary>正在倒数。</summary>
    Running,

    /// <summary>暂停中（剩余时间保留）。</summary>
    Paused,

    /// <summary>已经走完（等着被确认 / 重置）。</summary>
    Finished,
}

/// <summary>
/// 倒计时状态机（**纯逻辑**：不碰界面、不读系统时钟，时间由调用方喂）。
///
/// <para>为什么这样设计：自检可以任意跳时间断言「到点那一刻」而不用真的等，
/// 界面也只需要每个刷新周期把当前时间喂进来。</para>
///
/// <para>行为约定：</para>
/// <list type="bullet">
///   <item><see cref="Start"/>：停着就按当前设定的时长从头开始；暂停中接着走；正在走再按一次不重置；</item>
///   <item><see cref="Pause"/>：正在走才暂停；</item>
///   <item><see cref="Reset"/>：回到停着、剩余时间 = 设定时长；</item>
///   <item><see cref="AddTime"/>：±N 秒（正在走 / 暂停都能加，停着也改设定时长）；</item>
///   <item>走完时 <see cref="State"/> 变 <see cref="CountdownState.Finished"/>，
///         <see cref="JustFinished"/> 只在「刚走完那一次 Pulse」为真（界面据此弹一次通知）。</item>
/// </list>
/// </summary>
public sealed class CountdownEngine
{
    /// <summary>允许的最短时长（秒）。</summary>
    public const int MinSeconds = 1;

    /// <summary>允许的最长时长（秒）= 99 小时；再长也没人用倒计时了，改用闹钟。</summary>
    public const int MaxSeconds = 99 * 3600;

    private TimeSpan _configured;

    /// <summary>本次「继续」的时刻（只在 Running 时有意义）。</summary>
    private TimeSpan _resumedAt;

    /// <summary>暂停时保留的剩余时间。</summary>
    private TimeSpan _remaining;

    /// <summary>构造：默认 5 分钟。</summary>
    public CountdownEngine(TimeSpan? configured = null)
    {
        _configured = Clamp(configured ?? TimeSpan.FromMinutes(5));
        _remaining = _configured;
    }

    /// <summary>当前状态。</summary>
    public CountdownState State { get; private set; } = CountdownState.Idle;

    /// <summary>设定的总时长（改它会同时重置剩余时间，前提是没在跑）。</summary>
    public TimeSpan Configured
    {
        get => _configured;
        set
        {
            _configured = Clamp(value);
            if (State == CountdownState.Idle) _remaining = _configured;
        }
    }

    /// <summary>还剩多少（按 <paramref name="now"/> 现算）。</summary>
    public TimeSpan RemainingAt(TimeSpan now)
    {
        if (State != CountdownState.Running) return _remaining;

        var elapsed = now - _resumedAt;
        var remaining = _remaining - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>已走完的比例（0~1，进度环用）。</summary>
    public double ProgressAt(TimeSpan now)
    {
        if (_configured <= TimeSpan.Zero) return 1;

        var remaining = RemainingAt(now).TotalSeconds;
        var total = _configured.TotalSeconds;
        return Math.Clamp(1 - remaining / total, 0, 1);
    }

    /// <summary>这一次 Pulse 是不是「刚走完」（界面据此弹一次通知 / 响一次铃）。</summary>
    public bool JustFinished { get; private set; }

    /// <summary>是不是在走。</summary>
    public bool IsRunning => State == CountdownState.Running;

    /// <summary>是不是停着。</summary>
    public bool IsIdle => State == CountdownState.Idle;

    /// <summary>是不是暂停。</summary>
    public bool IsPaused => State == CountdownState.Paused;

    /// <summary>是不是已经走完（等确认）。</summary>
    public bool IsFinished => State == CountdownState.Finished;

    /// <summary>
    /// 把「当前时间」喂进来：刷新剩余时间，并检测是否走到 0。
    /// 返回「这一次是否刚刚走完」。
    /// </summary>
    public bool Pulse(TimeSpan now)
    {
        JustFinished = false;
        if (State != CountdownState.Running) return false;

        var remaining = RemainingAt(now);
        if (remaining > TimeSpan.Zero)
        {
            _remaining = remaining;
            _resumedAt = now;
            return false;
        }

        // 到点
        _remaining = TimeSpan.Zero;
        State = CountdownState.Finished;
        JustFinished = true;
        return true;
    }

    /// <summary>开始 / 继续。返回是否真的改变了状态。</summary>
    public bool Start(TimeSpan now)
    {
        if (State == CountdownState.Running) return false;

        // 走完了再按开始 = 重新开始（按设定时长）
        if (State == CountdownState.Finished) _remaining = _configured;
        if (_remaining <= TimeSpan.Zero) _remaining = _configured;

        _resumedAt = now;
        State = CountdownState.Running;
        return true;
    }

    /// <summary>暂停。返回是否真的改变了状态。</summary>
    public bool Pause(TimeSpan now)
    {
        if (State != CountdownState.Running) return false;

        _remaining = RemainingAt(now);
        State = CountdownState.Paused;
        return true;
    }

    /// <summary>暂停 / 继续切换（一个键搞定）。返回操作后的状态。</summary>
    public CountdownState TogglePause(TimeSpan now)
    {
        if (State == CountdownState.Running) Pause(now);
        else Start(now);
        return State;
    }

    /// <summary>回到停着，剩余时间 = 设定时长。</summary>
    public void Reset()
    {
        State = CountdownState.Idle;
        _remaining = _configured;
        _resumedAt = TimeSpan.Zero;
        JustFinished = false;
    }

    /// <summary>
    /// 加减时间（<paramref name="delta"/> 可为负）。
    /// <list type="bullet">
    ///   <item>正在走 / 暂停：改剩余时间（暂停时同时把「剩余」记下来）；</item>
    ///   <item>停着 / 走完：改设定时长（走完状态下加时会顺便回到暂停，方便再继续）。</item>
    /// </list>
    /// 返回实际生效的剩余时间。
    /// </summary>
    public TimeSpan AddTime(TimeSpan now, TimeSpan delta)
    {
        switch (State)
        {
            case CountdownState.Running:
                _remaining = RemainingAt(now);
                _remaining = ClampRemaining(_remaining + delta);
                _resumedAt = now;

                // 加时把已经走完的状态救回来
                if (_remaining > TimeSpan.Zero) State = CountdownState.Running;
                return _remaining;

            case CountdownState.Paused:
                _remaining = ClampRemaining(_remaining + delta);
                if (_remaining <= TimeSpan.Zero)
                {
                    _remaining = TimeSpan.Zero;
                    State = CountdownState.Finished;
                }
                return _remaining;

            default:
                _configured = Clamp(_configured + delta);
                _remaining = _configured;
                return _remaining;
        }
    }

    /// <summary>状态的中文名。</summary>
    public string StateText => State switch
    {
        CountdownState.Running => "倒计时中",
        CountdownState.Paused => "已暂停",
        CountdownState.Finished => "时间到",
        _ => "已就绪",
    };

    private static TimeSpan Clamp(TimeSpan value)
    {
        var seconds = Math.Clamp((int)Math.Round(value.TotalSeconds), MinSeconds, MaxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan ClampRemaining(TimeSpan value)
    {
        var seconds = Math.Clamp((int)Math.Round(value.TotalSeconds), 0, MaxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    // ---------------------------------------------------------------- 格式化 / 解析

    /// <summary>
    /// 倒计时显示：<c>mm:ss</c>（不足 1 小时）或 <c>h:mm:ss</c>。
    /// </summary>
    /// <param name="remaining">剩余时长。</param>
    /// <param name="showHours">是否强制显示小时位（设定超过 1 小时时用）。</param>
    public static string Format(TimeSpan remaining, bool showHours = false)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        var totalSeconds = (int)Math.Ceiling(remaining.TotalSeconds);   // 向上取整：29.4 秒要显示 00:30，不能提前显示 0
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;

        return showHours || hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{seconds:00}");
    }

    /// <summary>
    /// 解析用户输入的时长：<c>90</c>（秒）、<c>5:30</c>（分:秒）、<c>1:02:03</c>（时:分:秒）、
    /// <c>5m</c> / <c>90s</c> / <c>1h</c> / <c>1h30m</c> 都认。
    /// </summary>
    /// <returns>解析成功返回时长，失败返回 null（界面提示「写法看不懂」而不是静默成 0）。</returns>
    public static TimeSpan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim().ToLowerInvariant().Replace('：', ':');

        // 带单位：1h30m / 90s / 5m
        if (value.Any(char.IsLetter))
        {
            var total = TimeSpan.Zero;
            var number = "";
            var matched = false;

            foreach (var ch in value)
            {
                if (char.IsDigit(ch) || ch == '.')
                {
                    number += ch;
                    continue;
                }

                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                {
                    return null;
                }

                total += ch switch
                {
                    'h' or '时' or '小' => TimeSpan.FromHours(amount),
                    'm' or '分' or '钟' => TimeSpan.FromMinutes(amount),
                    's' or '秒' => TimeSpan.FromSeconds(amount),
                    _ => TimeSpan.MinValue,   // 不认识的单位
                };

                if (total == TimeSpan.MinValue) return null;
                number = "";
                matched = true;
            }

            if (number.Length > 0) return null;   // 以数字结尾却没单位（例如 "1h30"）
            return matched ? ClampToRange(total) : null;
        }

        // 冒号分隔：分:秒 或 时:分:秒
        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            if (parts.Length is < 2 or > 3) return null;
            if (parts.Any(p => !int.TryParse(p, out var n) || n < 0)) return null;

            var numbers = parts.Select(int.Parse).ToArray();
            var total = numbers.Length == 2
                ? TimeSpan.FromMinutes(numbers[0]) + TimeSpan.FromSeconds(numbers[1])
                : TimeSpan.FromHours(numbers[0]) + TimeSpan.FromMinutes(numbers[1]) + TimeSpan.FromSeconds(numbers[2]);

            return ClampToRange(total);
        }

        // 纯数字 = 秒
        return int.TryParse(value, out var seconds) && seconds >= 0
            ? ClampToRange(TimeSpan.FromSeconds(seconds))
            : null;
    }

    private static TimeSpan ClampToRange(TimeSpan value)
    {
        var seconds = Math.Clamp((int)Math.Round(value.TotalSeconds), MinSeconds, MaxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>把时长写成「1 小时 30 分」这种给人看的文字（界面上的说明用）。</summary>
    public static string Describe(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;

        var hours = (int)value.TotalHours;
        var minutes = value.Minutes;
        var seconds = value.Seconds;

        var parts = new List<string>();
        if (hours > 0) parts.Add($"{hours} 小时");
        if (minutes > 0) parts.Add($"{minutes} 分");
        if (seconds > 0) parts.Add($"{seconds} 秒");

        return parts.Count == 0 ? "0 秒" : string.Join(" ", parts);
    }
}
