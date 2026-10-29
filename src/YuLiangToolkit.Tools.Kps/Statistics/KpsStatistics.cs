using System.Diagnostics;

namespace YuLiangToolkit.Tools.Kps.Statistics;

/// <summary>
/// KPS 统计引擎（纯逻辑、线程安全、时钟可注入，自检直接喂假时间断言）。
///
/// <para><b>口径</b>（对齐 DmNote 的 keyStatsService）：</para>
/// <list type="bullet">
///   <item><b>当前 KPS</b> = 最近 1000ms 内「物理按下」的次数（1 秒滑动窗口，整数）；</item>
///   <item><b>峰值 KPS</b> = 每 50ms 采样一次当前 KPS 的历史最大值；</item>
///   <item><b>平均 KPS</b> = <b>只对「当前 KPS &gt; 0」的采样点</b>取算术平均（不是总次数 ÷ 总时长，
///         否则空闲时间会把平均值稀释——和 DmNote 一致）；</item>
///   <item><b>折线图</b> = 每 100ms 采一个「当前 KPS」点，点数 = 图表秒数 × 10（动态纵轴，见 <see cref="KpsSnapshot.GraphMax"/>）。</item>
/// </list>
///
/// <para>只统计「按下」（抬起只记 <see cref="TotalReleases"/>）；长按自动重复由输入层过滤。
/// 每 <see cref="Tick"/> 由 UI 定时器调用（建议 50ms）：采样平均 / 峰值，每 100ms 追加一个折线图点。</para>
/// </summary>
public sealed class KpsStatistics
{
    /// <summary>当前 KPS 的滚动窗口（毫秒）。</summary>
    public const int KpsWindowMs = 1000;

    /// <summary>折线图采样间隔（毫秒）；与 DmNote 的 graph 一致。</summary>
    public const int GraphSampleMs = 100;

    /// <summary>默认折线图容量（100ms × 300 = 30 秒）。</summary>
    public const int DefaultHistoryCapacity = 300;

    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private readonly Queue<long> _pressTimes = new();
    private readonly Dictionary<int, long> _keyCounts = new();
    private readonly List<double> _history = new();
    private int _historyCapacity;

    private long _startedAtMs;
    private long _totalPresses;
    private long _totalReleases;
    private double _peakKps;
    private double _kpsSum;
    private long _kpsSamples;
    private long _lastPressAtMs;
    private long _lastHistoryAtMs;

    /// <param name="historyCapacity">折线图点数上限（默认 300 = 30 秒 × 10 点/秒）。</param>
    /// <param name="clock">时钟（毫秒、单调递增）；默认取 <see cref="Environment.TickCount64"/>，自检可注入假时钟。</param>
    public KpsStatistics(int historyCapacity = DefaultHistoryCapacity, Func<long>? clock = null)
    {
        _historyCapacity = Math.Clamp(historyCapacity, 10, 6000);
        _clock = clock ?? (() => Environment.TickCount64);
        _startedAtMs = _clock();
        _lastHistoryAtMs = _startedAtMs;
    }

    /// <summary>折线图点数上限（改图表时长时由 ViewModel 重设；会保留最近的点）。</summary>
    public int HistoryCapacity
    {
        get { lock (_gate) return _historyCapacity; }
        set
        {
            lock (_gate)
            {
                _historyCapacity = Math.Clamp(value, 10, 6000);
                TrimHistoryLocked();
            }
        }
    }

    /// <summary>总按下次数（物理按下，不是格子计数）。</summary>
    public long TotalPresses
    {
        get { lock (_gate) return _totalPresses; }
    }

    /// <summary>总抬起次数（一般 ≈ 按下次数，掉按键时可能少几个）。</summary>
    public long TotalReleases
    {
        get { lock (_gate) return _totalReleases; }
    }

    /// <summary>峰值 KPS（按 50ms 采样取最大值；未取整，显示时四舍五入）。</summary>
    public double PeakKps
    {
        get { lock (_gate) return _peakKps; }
    }

    /// <summary>统计时长（秒）：从创建 / 重置开始（只用于显示，不参与平均 KPS 计算）。</summary>
    public double ElapsedSeconds
    {
        get
        {
            lock (_gate)
            {
                var elapsed = _clock() - _startedAtMs;
                return elapsed <= 0 ? 0 : elapsed / 1000d;
            }
        }
    }

    /// <summary>平均 KPS（只对 KPS &gt; 0 的采样点取平均并四舍五入，口径同 DmNote）。</summary>
    public double AverageKps
    {
        get
        {
            lock (_gate)
            {
                if (_kpsSamples == 0) return 0;
                return Math.Round(_kpsSum / _kpsSamples, MidpointRounding.AwayFromZero);
            }
        }
    }

    /// <summary>当前 KPS（最近 1000ms 的按下次数）。</summary>
    public double CurrentKps
    {
        get { lock (_gate) return CurrentKpsLocked(_clock()); }
    }

    /// <summary>记录一次按下（输入层过滤掉长按自动重复后调用；任意线程可调）。</summary>
    public void RegisterPress(int virtualKey)
    {
        lock (_gate)
        {
            var now = _clock();
            _pressTimes.Enqueue(now);
            _totalPresses++;
            _lastPressAtMs = now;
            if (_keyCounts.TryGetValue(virtualKey, out var count)) _keyCounts[virtualKey] = count + 1;
            else _keyCounts[virtualKey] = 1;
            TrimPressTimesLocked(now);
        }
    }

    /// <summary>记录一次抬起（只计数，不影响 KPS）。</summary>
    public void RegisterRelease(int virtualKey)
    {
        lock (_gate) _totalReleases++;
    }

    /// <summary>
    /// UI 定时器（建议 50ms）调用：采样当前 KPS（更新峰值 / 平均），每 <see cref="GraphSampleMs"/> 追加一个折线图点。
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            var now = _clock();
            var current = CurrentKpsLocked(now);
            if (current > _peakKps) _peakKps = current;
            if (current > 0)
            {
                _kpsSum += current;
                _kpsSamples++;
            }

            if (now - _lastHistoryAtMs >= GraphSampleMs)
            {
                _history.Add(current);
                _lastHistoryAtMs = now;
                TrimHistoryLocked();
            }

            TrimPressTimesLocked(now);
        }
    }

    /// <summary>取当前所有统计的快照（UI 定时器每 50/100ms 调一次，避免多次加锁拿到互相矛盾的数）。</summary>
    public KpsSnapshot Capture()
    {
        lock (_gate)
        {
            var now = _clock();
            var current = CurrentKpsLocked(now);
            var average = _kpsSamples == 0 ? 0 : Math.Round(_kpsSum / _kpsSamples, MidpointRounding.AwayFromZero);
            var elapsed = Math.Max(0, now - _startedAtMs) / 1000d;

            var max = 1d;
            foreach (var value in _history)
            {
                if (value > max) max = value;
            }
            if (current > max) max = current;
            if (_peakKps > max) max = _peakKps;

            return new KpsSnapshot(
                current,
                _peakKps,
                average,
                _totalPresses,
                _totalReleases,
                elapsed,
                _history.ToArray(),
                Math.Ceiling(max));
        }
    }

    /// <summary>某个键的按下次数（物理按下口径）。</summary>
    public long GetKeyPressCount(int virtualKey)
    {
        lock (_gate) return _keyCounts.TryGetValue(virtualKey, out var count) ? count : 0;
    }

    /// <summary>所有键的按下次数快照（持久化计数器 / 诊断用）。</summary>
    public IReadOnlyDictionary<int, long> GetKeyCounts()
    {
        lock (_gate) return new Dictionary<int, long>(_keyCounts);
    }

    /// <summary>最近一次按下的时间（毫秒时间戳；没按过 = 0）。</summary>
    public long LastPressAtMs
    {
        get { lock (_gate) return _lastPressAtMs; }
    }

    /// <summary>清零：计数、峰值、平均、折线图、统计时长全部从头开始。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pressTimes.Clear();
            _keyCounts.Clear();
            _history.Clear();
            _totalPresses = 0;
            _totalReleases = 0;
            _peakKps = 0;
            _kpsSum = 0;
            _kpsSamples = 0;
            _lastPressAtMs = 0;
            _startedAtMs = _clock();
            _lastHistoryAtMs = _startedAtMs;
        }
    }

    // ---------- 内部 ----------

    private double CurrentKpsLocked(long now)
    {
        if (_pressTimes.Count == 0) return 0;
        var since = now - KpsWindowMs;
        var count = 0;
        foreach (var time in _pressTimes)
        {
            if (time > since) count++;
        }
        return count;
    }

    /// <summary>丢掉 1 秒窗口之外的按键时间戳（保留 2 秒余量，够做滚动窗口）。</summary>
    private void TrimPressTimesLocked(long now)
    {
        var cutoff = now - KpsWindowMs * 2L;
        while (_pressTimes.Count > 0 && _pressTimes.Peek() < cutoff) _pressTimes.Dequeue();
    }

    private void TrimHistoryLocked()
    {
        var overflow = _history.Count - _historyCapacity;
        if (overflow > 0) _history.RemoveRange(0, overflow);
    }
}

/// <summary>一次统计快照：UI 每 50/100ms 拿它刷新数字 / 折线图，避免读一半被改。</summary>
public readonly record struct KpsSnapshot(
    double CurrentKps,
    double PeakKps,
    double AverageKps,
    long TotalPresses,
    long TotalReleases,
    double ElapsedSeconds,
    IReadOnlyList<double> GraphValues,
    double GraphMax)
{
    /// <summary>空快照（还没开始统计时显示用）。</summary>
    public static KpsSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, Array.Empty<double>(), 1);
}
