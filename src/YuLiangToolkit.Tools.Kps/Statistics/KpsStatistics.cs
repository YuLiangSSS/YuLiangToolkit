namespace YuLiangToolkit.Tools.Kps.Statistics;

/// <summary>
/// KPS 统计引擎（纯逻辑、线程安全、时钟可注入：自检直接喂假时钟做 1ms 精度的断言）。
///
/// <para><b>统计口径 —— 对齐 DmNote 2.1.0 的 keyStatsService：</b></para>
/// <list type="number">
///   <item><b>当前 KPS</b> = 最近 <see cref="KpsWindowMs"/>（1000）毫秒内「物理按下」的次数，
///         整数、1 秒滑动窗口；长按自动重复由输入层过滤，这里只会收到真正的按下。</item>
///   <item><b>峰值 KPS</b> = 每次 <see cref="Tick"/>（调用方每 50ms 调一次）采样到的当前 KPS 的历史最大值。
///         <b>按键事件不更新峰值</b>，否则会和 DmNote 的采样口径不一致。</item>
///   <item><b>平均 KPS</b> = <b>只对「当前 KPS &gt; 0」的 Tick 采样点</b>取算术平均，最后
///         <c>Math.Round(..., MidpointRounding.AwayFromZero)</c> 取整（不是「总次数 ÷ 总时长」——
///         空闲时间不参与计算，也就不会把平均值稀释）。</item>
///   <item><b>折线图</b> = 每 <see cref="GraphSampleMs"/>（100）毫秒追加一个当前 KPS 点，
///         容量 = 图表秒数 × 10（默认 <see cref="DefaultHistoryCapacity"/> = 300 点 = 30 秒）；
///         纵轴见 <see cref="KpsSnapshot.GraphMax"/>（动态计算，界面不要写死上限）。</item>
/// </list>
///
/// <para><b>线程安全</b>：输入事件可能来自 Raw Input 线程，UI 定时器在 UI 线程调 <see cref="Tick"/> /
/// <see cref="Capture"/>，所有状态都在同一把锁里读写；<see cref="Capture"/> 返回的快照（含折线图数组）
/// 是副本，拿走后随便读、随便改都不会影响引擎。</para>
///
/// <para><b>时钟</b>：默认 <see cref="Environment.TickCount64"/>；自检可以注入假时钟（毫秒、单调递增）。
/// 构造 / <see cref="Reset"/> 时把统计起点记为当时的时钟值，因此假时钟从很大的数字开始也不会出现
/// 负时长 / 除零；时钟万一回拨，<see cref="ElapsedSeconds"/> 也只返回 0（不返回负数）。</para>
/// </summary>
public sealed class KpsStatistics
{
    /// <summary>当前 KPS 的滚动窗口（毫秒）。</summary>
    public const int KpsWindowMs = 1000;

    /// <summary>折线图采样间隔（毫秒）；与 DmNote 的 graph 一致。</summary>
    public const int GraphSampleMs = 100;

    /// <summary>默认折线图容量（100ms × 300 = 30 秒）。</summary>
    public const int DefaultHistoryCapacity = 300;

    /// <summary>折线图容量下限（再小画出来没意义）。</summary>
    private const int MinHistoryCapacity = 10;

    /// <summary>折线图容量上限（120 秒 × 10 = 1200；留余量到 6000，防手改配置写成天文数字）。</summary>
    private const int MaxHistoryCapacity = 6000;

    private readonly object _gate = new();
    private readonly Func<long> _clock;

    /// <summary>窗口内的按键时间戳（毫秒）。只存最近 1 秒左右，不存全部历史。</summary>
    private readonly Queue<long> _pressTimes = new();

    /// <summary>每键累计按下次数（虚拟键码 → 次数）。</summary>
    private readonly Dictionary<int, long> _keyCounts = new();

    /// <summary>折线图采样点（每 100ms 一个，最多 <see cref="_historyCapacity"/> 个）。</summary>
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
        _historyCapacity = Math.Clamp(historyCapacity, MinHistoryCapacity, MaxHistoryCapacity);
        _clock = clock ?? (() => Environment.TickCount64);
        var now = _clock();
        _startedAtMs = now;
        _lastHistoryAtMs = now;
    }

    /// <summary>
    /// 折线图点数上限（改图表时长时由 ViewModel 重设；调小会立刻丢掉最旧的点）。
    /// 一般等于「图表秒数 × 10」：30 秒 → 300 点。
    /// </summary>
    public int HistoryCapacity
    {
        get { lock (_gate) return _historyCapacity; }
        set
        {
            lock (_gate)
            {
                _historyCapacity = Math.Clamp(value, MinHistoryCapacity, MaxHistoryCapacity);
                TrimHistoryLocked();
            }
        }
    }

    /// <summary>总按下次数（物理按下，不是格子计数）。</summary>
    public long TotalPresses
    {
        get { lock (_gate) return _totalPresses; }
    }

    /// <summary>总抬起次数（一般 ≈ 按下次数；系统丢键时可能少几个）。</summary>
    public long TotalReleases
    {
        get { lock (_gate) return _totalReleases; }
    }

    /// <summary>峰值 KPS（每次 <see cref="Tick"/> 采样取历史最大；不取整，显示时四舍五入）。</summary>
    public double PeakKps
    {
        get { lock (_gate) return _peakKps; }
    }

    /// <summary>统计时长（秒）：从创建 / <see cref="Reset"/> 开始（只用于显示，不参与平均 KPS 计算）。</summary>
    public double ElapsedSeconds
    {
        get
        {
            lock (_gate) return ElapsedSecondsLocked(_clock());
        }
    }

    /// <summary>
    /// 平均 KPS：只对「当前 KPS &gt; 0」的 Tick 采样点取算术平均，再按
    /// <see cref="MidpointRounding.AwayFromZero"/> 四舍五入（口径同 DmNote）。
    /// </summary>
    public double AverageKps
    {
        get { lock (_gate) return AverageKpsLocked(); }
    }

    /// <summary>当前 KPS：最近 1000ms 内物理按下的次数（1 秒滑动窗口；等于 1000ms 的按键不算）。</summary>
    public double CurrentKps
    {
        get { lock (_gate) return CurrentKpsLocked(_clock()); }
    }

    /// <summary>最近一次按下的时间（毫秒时间戳；没按过 / 刚 <see cref="Reset"/> 后 = 0）。</summary>
    public long LastPressAtMs
    {
        get { lock (_gate) return _lastPressAtMs; }
    }

    /// <summary>
    /// 记录一次按下（输入层过滤掉长按自动重复后调用；任意线程可调）。
    /// 只入窗口队列 + 累计计数，<b>不更新峰值 / 平均 / 折线图</b>（那些只在 Tick 采样时更新）。
    /// </summary>
    public void RegisterPress(int virtualKey)
    {
        lock (_gate)
        {
            var now = _clock();
            _pressTimes.Enqueue(now);
            TrimPressWindowLocked(now);

            _totalPresses++;
            _lastPressAtMs = now;
            _keyCounts.TryGetValue(virtualKey, out var count);
            _keyCounts[virtualKey] = count + 1;
        }
    }

    /// <summary>
    /// 记录一次抬起（只计 <see cref="TotalReleases"/>，不影响 KPS；参数留给以后做每键抬起统计 / 诊断）。
    /// </summary>
    public void RegisterRelease(int virtualKey)
    {
        lock (_gate) _totalReleases++;
    }

    /// <summary>
    /// UI 定时器（建议 50ms）调用：采样当前 KPS —— 更新峰值、累加平均采样；每
    /// <see cref="GraphSampleMs"/> 毫秒再往折线图追加一个点。
    ///
    /// <para>⚠ 一次调用 = 一次采样。调用方请保持稳定的 50ms 节奏；如果 UI 线程被卡住导致 Tick 变少，
    /// 平均 / 峰值只会少采样，不会补造数据。</para>
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            var now = _clock();
            var current = CurrentKpsLocked(now);

            // 峰值：只在这里采样（按键事件里不更新，保证和 DmNote 的 50ms 采样口径一致）。
            if (current > _peakKps) _peakKps = current;

            // 平均：只累加 KPS > 0 的采样点，空闲时间不稀释平均值。
            if (current > 0)
            {
                _kpsSum += current;
                _kpsSamples++;
            }

            // 折线图：每 100ms 追加一个点；调用方掉拍时只少采样，不补假点。
            if (now - _lastHistoryAtMs >= GraphSampleMs)
            {
                _history.Add(current);
                _lastHistoryAtMs = now;
                TrimHistoryLocked();
            }

            TrimPressWindowLocked(now);
        }
    }

    /// <summary>
    /// 取当前所有统计的快照（UI 定时器每 50/100ms 调一次，避免多次加锁拿到互相矛盾的数）。
    /// 折线图返回的是数组副本（<see cref="KpsSnapshot.GraphValues"/>），随便读 / 改都不影响引擎。
    /// </summary>
    public KpsSnapshot Capture()
    {
        lock (_gate)
        {
            var now = _clock();
            var current = CurrentKpsLocked(now);

            // 动态纵轴：历史点 / 当前值 / 峰值取最大，再向上取整，最小 1（空数据时也不除以 0）。
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
                AverageKpsLocked(),
                _totalPresses,
                _totalReleases,
                ElapsedSecondsLocked(now),
                _history.ToArray(),
                Math.Ceiling(max));
        }
    }

    /// <summary>某个键的按下次数（物理按下口径；没按过 = 0）。</summary>
    public long GetKeyPressCount(int virtualKey)
    {
        lock (_gate) return _keyCounts.TryGetValue(virtualKey, out var count) ? count : 0;
    }

    /// <summary>所有键的按下次数快照（持久化计数器 / 诊断用；返回的是副本）。</summary>
    public IReadOnlyDictionary<int, long> GetKeyCounts()
    {
        lock (_gate) return new Dictionary<int, long>(_keyCounts);
    }

    /// <summary>
    /// 清零：计数、每键计数、峰值、平均、折线图全部从头开始，统计起点 / 折线图采样起点设为当前时钟
    /// （<see cref="ElapsedSeconds"/> 也从 0 重新计时）。
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            var now = _clock();
            _pressTimes.Clear();
            _keyCounts.Clear();
            _history.Clear();
            _totalPresses = 0;
            _totalReleases = 0;
            _peakKps = 0;
            _kpsSum = 0;
            _kpsSamples = 0;
            _lastPressAtMs = 0;
            _startedAtMs = now;
            _lastHistoryAtMs = now;
        }
    }

    // ---------- 内部（调用方已持锁） ----------

    /// <summary>最近 1000ms 内的按下次数；窗口边界（正好 1000ms 前）不算，所以用严格大于。</summary>
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

    /// <summary>平均 KPS：只对 KPS &gt; 0 的采样点取平均，再四舍五入（无采样点 = 0）。</summary>
    private double AverageKpsLocked()
        => _kpsSamples == 0 ? 0 : Math.Round(_kpsSum / _kpsSamples, MidpointRounding.AwayFromZero);

    /// <summary>统计时长（秒）；时钟回拨时返回 0，绝不返回负数。</summary>
    private double ElapsedSecondsLocked(long now)
    {
        var elapsed = now - _startedAtMs;
        return elapsed > 0 ? elapsed / 1000d : 0;
    }

    /// <summary>
    /// 丢掉 1 秒窗口之外的按键时间戳（按注册顺序入队，队头最旧；窗口边界上的那个先留着，
    /// 计数时用严格大于把它排除）。这样内存只随「最近 1 秒的按键数」增长，不会无限堆积。
    /// </summary>
    private void TrimPressWindowLocked(long now)
    {
        var cutoff = now - KpsWindowMs;
        while (_pressTimes.Count > 0 && _pressTimes.Peek() < cutoff) _pressTimes.Dequeue();
    }

    /// <summary>折线图超出容量时丢掉最旧的点（保留最近 <see cref="_historyCapacity"/> 个）。</summary>
    private void TrimHistoryLocked()
    {
        var overflow = _history.Count - _historyCapacity;
        if (overflow > 0) _history.RemoveRange(0, overflow);
    }
}

/// <summary>一次统计快照：UI 每 50/100ms 拿它刷新数字 / 折线图，避免读一半被改。</summary>
/// <param name="CurrentKps">最近 1000ms 内的按下次数。</param>
/// <param name="PeakKps">Tick 采样到的历史最大 KPS。</param>
/// <param name="AverageKps">只算 KPS &gt; 0 的采样点的平均 KPS（已四舍五入）。</param>
/// <param name="TotalPresses">总按下次数。</param>
/// <param name="TotalReleases">总抬起次数。</param>
/// <param name="ElapsedSeconds">统计时长（秒，只用于显示）。</param>
/// <param name="GraphValues">折线图采样点副本（每 100ms 一个，最多 HistoryCapacity 个）。</param>
/// <param name="GraphMax">折线图纵轴上限 = ceil(max(历史点, 当前值, 峰值, 1))。</param>
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
    /// <summary>空快照（还没开始统计时显示用；纵轴至少 1，避免除以 0）。</summary>
    public static KpsSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, Array.Empty<double>(), 1);
}
