using System.Globalization;

namespace YuLiangToolkit.Tools.Stopwatch;

/// <summary>秒表当前状态。</summary>
public enum StopwatchState
{
    /// <summary>停着（没开始，或已停止归零）。</summary>
    Idle,

    /// <summary>正在计时。</summary>
    Running,

    /// <summary>暂停中（时间保留，继续后接着走）。</summary>
    Paused,
}

/// <summary>一次计次（分段）记录。</summary>
/// <param name="Index">第几次（从 1 开始）。</param>
/// <param name="Total">按下计次时的总时长。</param>
/// <param name="Split">与上一次计次之间的间隔（第一次 = 总时长）。</param>
public readonly record struct StopwatchLap(int Index, TimeSpan Total, TimeSpan Split);

/// <summary>
/// 秒表计时状态机（**纯逻辑**：不碰界面、不读系统时钟，时间由调用方喂进来）。
///
/// <para>这样自检可以任意跳时间断言，而不用真的等 3 秒。界面每帧/每秒调
/// <see cref="Advance"/> 或直接读 <see cref="Elapsed"/> 即可。</para>
///
/// <para>行为约定：</para>
/// <list type="bullet">
///   <item><see cref="Start"/>：停着就从 0 开始，暂停中就接着走，正在走再按一次什么都不做（幂等）；</item>
///   <item><see cref="Pause"/>：正在走才暂停（其他状态忽略）；</item>
///   <item><see cref="Stop"/>：任何状态都归零回到停着，并且清空计次（和实体秒表的「复位」一样）；</item>
///   <item><see cref="Lap"/>：只有正在走 / 暂停时才能计次；</item>
///   <item>暂停期间时间不增长。</item>
/// </list>
/// </summary>
public sealed class StopwatchSession
{
    /// <summary>已经累计的时长（暂停时不再增长）。</summary>
    private TimeSpan _accumulated;

    /// <summary>本次「继续」的时间点（只在 Running 时有意义）。</summary>
    private TimeSpan _resumedAt;

    private readonly List<StopwatchLap> _laps = new();

    /// <summary>当前状态。</summary>
    public StopwatchState State { get; private set; } = StopwatchState.Idle;

    /// <summary>当前累计时长（按 <paramref name="now"/> 现算，暂停 / 停着时就是累计值）。</summary>
    public TimeSpan ElapsedAt(TimeSpan now)
    {
        if (State != StopwatchState.Running) return _accumulated;
        var delta = now - _resumedAt;
        return delta > TimeSpan.Zero ? _accumulated + delta : _accumulated;
    }

    /// <summary>
    /// 当前累计时长。**需要调用方先 <see cref="Pulse"/> 过**（或者直接用 <see cref="ElapsedAt"/>）。
    /// 这样状态机本身不依赖任何全局时钟，自检完全可控。
    /// </summary>
    public TimeSpan Elapsed => _accumulated;

    /// <summary>正在计时。</summary>
    public bool IsRunning => State == StopwatchState.Running;

    /// <summary>停着（没开始 / 已归零）。</summary>
    public bool IsIdle => State == StopwatchState.Idle;

    /// <summary>暂停中。</summary>
    public bool IsPaused => State == StopwatchState.Paused;

    /// <summary>计次记录（最新在后）。</summary>
    public IReadOnlyList<StopwatchLap> Laps => _laps;

    /// <summary>把「当前时间」喂进来刷新累计值（界面每个刷新周期调一次）。</summary>
    public void Pulse(TimeSpan now)
    {
        if (State != StopwatchState.Running) return;

        // ⚠ 必须同时把基准时间挪到 now：只累加不回挪的话，下次 ElapsedAt 会把
        // 「从上次开始到现在」再算一遍（计次总时长直接翻倍/跳变）。
        _accumulated = ElapsedAt(now);
        _resumedAt = now;
    }

    /// <summary>开始 / 继续（幂等：正在走时再按什么都不做）。返回是否真的改变了状态。</summary>
    public bool Start(TimeSpan now)
    {
        if (State == StopwatchState.Running) return false;

        _resumedAt = now;
        State = StopwatchState.Running;
        return true;
    }

    /// <summary>暂停。返回是否真的改变了状态（停着 / 已暂停时返回 false）。</summary>
    public bool Pause(TimeSpan now)
    {
        if (State != StopwatchState.Running) return false;

        // 先把当前时间折进累计值，再把状态切成暂停（切完 ElapsedAt 就只读累计值了）
        _accumulated = ElapsedAt(now);
        _resumedAt = now;
        State = StopwatchState.Paused;
        return true;
    }

    /// <summary>停止并归零（清空计次）。返回是否真的改变了状态（本来就停着且是 0 时返回 false）。</summary>
    public bool Stop()
    {
        var changed = State != StopwatchState.Idle || _accumulated > TimeSpan.Zero || _laps.Count > 0;

        State = StopwatchState.Idle;
        _accumulated = TimeSpan.Zero;
        _resumedAt = TimeSpan.Zero;
        _laps.Clear();
        return changed;
    }

    /// <summary>记一次计次。返回新记录（停着时返回 null）。</summary>
    public StopwatchLap? Lap(TimeSpan now)
    {
        if (State == StopwatchState.Idle) return null;

        Pulse(now);
        var total = _accumulated;
        var previous = _laps.Count > 0 ? _laps[^1].Total : TimeSpan.Zero;
        var lap = new StopwatchLap(_laps.Count + 1, total, total - previous);
        _laps.Add(lap);
        return lap;
    }

    /// <summary>清空计次（不影响计时）。</summary>
    public void ClearLaps() => _laps.Clear();

    /// <summary>状态的中文名（界面 / 通知文案用）。</summary>
    public string StateText => State switch
    {
        StopwatchState.Running => "计时中",
        StopwatchState.Paused => "已暂停",
        _ => "已停止",
    };

    // ---------------------------------------------------------------- 格式化

    /// <summary>
    /// 时长文本：不足 1 小时写 <c>mm:ss.cc</c>，超过写 <c>h:mm:ss.cc</c>；
    /// <paramref name="showMilliseconds"/> = false 时只到十分之一秒。
    /// </summary>
    public static string Format(TimeSpan elapsed, bool showMilliseconds = true)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        // 毫秒级向下取整（不四舍五入：秒表读数不能「提前」跳到下一秒）
        var totalMs = (long)elapsed.TotalMilliseconds;
        var hours = totalMs / 3_600_000;
        var minutes = totalMs % 3_600_000 / 60_000;
        var seconds = totalMs % 60_000 / 1000;
        var centi = totalMs % 1000 / 10;

        var fraction = showMilliseconds ? $".{centi:00}" : "";
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}{fraction}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{seconds:00}{fraction}");
    }

    /// <summary>毫秒级的更精细文本（<c>mm:ss.mmm</c>），导出 / 复制用。</summary>
    public static string FormatPrecise(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var totalMs = (long)elapsed.TotalMilliseconds;
        var hours = totalMs / 3_600_000;
        var minutes = totalMs % 3_600_000 / 60_000;
        var seconds = totalMs % 60_000 / 1000;
        var ms = totalMs % 1000;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}.{ms:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{seconds:00}.{ms:000}");
    }
}
