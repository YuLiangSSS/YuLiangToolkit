using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.ChatRoom.Traffic;

/// <summary>流量上限针对哪个方向（用户最怕的通常是「上传被跑满」）。</summary>
public enum TrafficQuotaScope
{
    /// <summary>上传 + 下载合计（最省心，也最保守）。</summary>
    Both = 0,

    /// <summary>只算上传（自己发出去的）。</summary>
    Upload = 1,

    /// <summary>只算下载（自己收到的）。</summary>
    Download = 2,

    /// <summary>不限制。</summary>
    Off = 3,
}

/// <summary>一次「能不能发」的判断结果（纯数据，界面直接显示 <see cref="Reason"/>）。</summary>
/// <param name="Allowed">允许发送。</param>
/// <param name="Reason">不允许的原因 / 允许时的说明。</param>
public readonly record struct QuotaVerdict(bool Allowed, string Reason);

/// <summary>
/// 单次流量计量与限额：<b>纯逻辑、无 IO、无网络</b>，所以自检能把边界（到顶 / 超一点点 / 反向不限）
/// 挨个断言一遍。
///
/// <para>两件必须做对的事：</para>
/// <list type="number">
///   <item><b>发送前预检</b>：不是等超了再报错，而是「这个文件比剩余额度大」就干脆不开数据连接
///         （用户要的就是“避免流量一次性使用过多”，传一半被掐断反而更糟）；</item>
///   <item><b>传输中实时掐断</b>：额度在传输过程中被别人用掉 / 用户临时调小，也要能立刻停下
///         （<see cref="Check"/> 每个数据块都调一次）。</item>
/// </list>
///
/// <para>速率统计用一个滑动窗口（<see cref="Snapshot"/> 每次读都会顺带把过期的采样点丢掉），
/// 界面上的「当前速度」就是它算的。</para>
/// </summary>
public sealed class TrafficMeter
{
    private readonly object _gate = new();
    private readonly Queue<(long Ticks, long Sent, long Received)> _samples = new();
    private readonly Func<long> _timestamp;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private long _sent;
    private long _received;
    private long _limitBytes;
    private TrafficQuotaScope _scope;
    private bool _limitNotified;

    /// <param name="limitBytes">上限字节数（&lt;= 0 表示不限）。</param>
    /// <param name="scope">上限针对的方向。</param>
    /// <param name="timestamp">取时间戳的函数（自检里换成假的，速率断言才稳定）。
    /// 注意 <see cref="Environment.TickCount64"/> 只有约 15.6 毫秒的分辨率，默认用 <see cref="System.Diagnostics.Stopwatch"/>。</param>
    public TrafficMeter(long limitBytes, TrafficQuotaScope scope, Func<long>? timestamp = null)
    {
        _limitBytes = Math.Max(0, limitBytes);
        _scope = scope;
        _timestamp = timestamp ?? (() => _clock.ElapsedMilliseconds);
    }

    /// <summary>本端已发出的字节数。</summary>
    public long SentBytes { get { lock (_gate) return _sent; } }

    /// <summary>本端已收到的字节数。</summary>
    public long ReceivedBytes { get { lock (_gate) return _received; } }

    /// <summary>收发合计。</summary>
    public long TotalBytes { get { lock (_gate) return _sent + _received; } }

    /// <summary>当前上限（0 = 不限）；用户可以中途改。</summary>
    public long LimitBytes { get { lock (_gate) return _limitBytes; } }

    /// <summary>当前上限方向。</summary>
    public TrafficQuotaScope Scope { get { lock (_gate) return _scope; } }

    /// <summary>中途改限额（设置面板里拖一下就生效，不需要重连）。</summary>
    public void SetLimit(long limitBytes, TrafficQuotaScope scope)
    {
        lock (_gate)
        {
            _limitBytes = Math.Max(0, limitBytes);
            _scope = scope;
            _limitNotified = false;
        }
    }

    /// <summary>记一笔已发送（<paramref name="bytes"/> 是这次真正写进 socket 的字节数）。</summary>
    public void AddSent(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            _sent += bytes;
            RecordSample();
        }
    }

    /// <summary>记一笔已收到。</summary>
    public void AddReceived(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            _received += bytes;
            RecordSample();
        }
    }

    /// <summary>按上限方向算「已经用掉的量」。</summary>
    public long UsedBytes
    {
        get
        {
            lock (_gate)
                return _scope switch
                {
                    TrafficQuotaScope.Upload => _sent,
                    TrafficQuotaScope.Download => _received,
                    TrafficQuotaScope.Off => 0,
                    _ => _sent + _received,
                };
        }
    }

    /// <summary>剩余额度（不限时是 <see cref="long.MaxValue"/>）。</summary>
    public long RemainingBytes
    {
        get
        {
            lock (_gate)
            {
                if (_scope == TrafficQuotaScope.Off || _limitBytes <= 0) return long.MaxValue;
                var used = _scope switch
                {
                    TrafficQuotaScope.Upload => _sent,
                    TrafficQuotaScope.Download => _received,
                    _ => _sent + _received,
                };
                return Math.Max(0, _limitBytes - used);
            }
        }
    }

    /// <summary>使用比例 0~1（不限时返回 0，界面进度条据此显示）。</summary>
    public double UsedRatio
    {
        get
        {
            lock (_gate)
            {
                if (_scope == TrafficQuotaScope.Off || _limitBytes <= 0) return 0;
                var used = _scope switch
                {
                    TrafficQuotaScope.Upload => _sent,
                    TrafficQuotaScope.Download => _received,
                    _ => _sent + _received,
                };
                return Math.Clamp((double)used / _limitBytes, 0, 1);
            }
        }
    }

    /// <summary>到顶了没有（不限时永远 false）。</summary>
    public bool IsExhausted
    {
        get
        {
            lock (_gate) return IsExhaustedUnlocked();
        }
    }

    /// <summary>
    /// 发送前预检：这次要发 <paramref name="bytes"/> 字节行不行。
    /// <paramref name="bytes"/> 传 0 = 只问「现在还能不能发东西」（例如发一条文字）。
    /// </summary>
    public QuotaVerdict Check(long bytes)
    {
        lock (_gate)
        {
            if (_scope == TrafficQuotaScope.Off || _limitBytes <= 0)
                return new QuotaVerdict(true, "未限制流量");

            var used = _scope switch
            {
                TrafficQuotaScope.Upload => _sent,
                TrafficQuotaScope.Download => _received,
                _ => _sent + _received,
            };
            var remaining = _limitBytes - used;
            if (remaining <= 0)
                return new QuotaVerdict(false, $"本次流量的{ScopeText()}已用满（{FileSize.Format(_limitBytes)}），请调大上限或重连");

            if (bytes > 0 && bytes > remaining)
                return new QuotaVerdict(false,
                    $"超出单次流量上限：这个文件 {FileSize.Format(bytes)}，剩余额度只有 {FileSize.Format(remaining)}");

            return new QuotaVerdict(true, $"剩余额度 {FileSize.Format(remaining)}");
        }
    }

    /// <summary>到顶这件事只提醒一次（界面 / 对方提示不要刷屏）。</summary>
    public bool ShouldNotifyExhausted()
    {
        lock (_gate)
        {
            if (!IsExhaustedUnlocked() || _limitNotified) return false;
            _limitNotified = true;
            return true;
        }
    }

    private bool IsExhaustedUnlocked()
    {
        if (_scope == TrafficQuotaScope.Off || _limitBytes <= 0) return false;
        var used = _scope switch
        {
            TrafficQuotaScope.Upload => _sent,
            TrafficQuotaScope.Download => _received,
            _ => _sent + _received,
        };
        return _limitBytes - used <= 0;
    }

    /// <summary>本次流量快照（界面一次刷新只取一次，别一个字段读一遍锁）。</summary>
    public TrafficSnapshot Snapshot()
    {
        lock (_gate)
        {
            var (sentRate, receivedRate) = ComputeRates();
            var used = _scope switch
            {
                TrafficQuotaScope.Upload => _sent,
                TrafficQuotaScope.Download => _received,
                TrafficQuotaScope.Off => 0L,
                _ => _sent + _received,
            };
            var limited = _scope != TrafficQuotaScope.Off && _limitBytes > 0;
            return new TrafficSnapshot(
                _sent, _received, _limitBytes, _scope, used,
                limited ? Math.Max(0, _limitBytes - used) : long.MaxValue,
                limited ? Math.Clamp((double)used / _limitBytes, 0, 1) : 0,
                sentRate, receivedRate,
                limited && _limitBytes - used <= 0);
        }
    }

    /// <summary>重置（重新开一次连接 = 新的一次流量）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _sent = 0;
            _received = 0;
            _samples.Clear();
            _limitNotified = false;
        }
    }

    private void RecordSample()
    {
        var now = _timestamp();
        _samples.Enqueue((now, _sent, _received));
        TrimSamples(now);
    }

    private void TrimSamples(long now)
    {
        while (_samples.Count > 2 && now - _samples.Peek().Ticks > RateWindowMs)
            _samples.Dequeue();
    }

    private const int RateWindowMs = 3000;

    private (double Sent, double Received) ComputeRates()
    {
        if (_samples.Count < 2) return (0, 0);
        var now = _timestamp();
        TrimSamples(now);

        var first = _samples.Peek();
        var spanTicks = now - first.Ticks;
        if (spanTicks <= 0) return (0, 0);

        var last = _samples.Last();
        var seconds = spanTicks / 1000.0;
        return ((last.Sent - first.Sent) / seconds, (last.Received - first.Received) / seconds);
    }

    private string ScopeText() => _scope switch
    {
        TrafficQuotaScope.Upload => "上传",
        TrafficQuotaScope.Download => "下载",
        _ => "收发合计",
    };
}

/// <summary>某一刻的流量快照（界面直接绑这几个字段）。</summary>
/// <param name="SentBytes">已上传字节。</param>
/// <param name="ReceivedBytes">已下载字节。</param>
/// <param name="LimitBytes">上限（0 = 不限）。</param>
/// <param name="Scope">上限方向。</param>
/// <param name="UsedBytes">按方向算已用。</param>
/// <param name="RemainingBytes">剩余（不限时 <see cref="long.MaxValue"/>）。</param>
/// <param name="UsedRatio">已用比例 0~1。</param>
/// <param name="SentRate">当前上传速度（字节/秒）。</param>
/// <param name="ReceivedRate">当前下载速度（字节/秒）。</param>
/// <param name="Exhausted">是否已到顶。</param>
public readonly record struct TrafficSnapshot(
    long SentBytes,
    long ReceivedBytes,
    long LimitBytes,
    TrafficQuotaScope Scope,
    long UsedBytes,
    long RemainingBytes,
    double UsedRatio,
    double SentRate,
    double ReceivedRate,
    bool Exhausted)
{
    /// <summary>是否设置了上限。</summary>
    public bool Limited => Scope != TrafficQuotaScope.Off && LimitBytes > 0;

    /// <summary>「↑ 1.2 MB  ↓ 3.4 MB」这种一行摘要。</summary>
    public string Summary => $"↑ 已发 {FileSize.Format(SentBytes)}　↓ 已收 {FileSize.Format(ReceivedBytes)}";

    /// <summary>上限那一行文案。</summary>
    public string LimitText => !Limited
        ? "未限制流量"
        : $"上限 {FileSize.Format(LimitBytes)}（{ScopeText}）　已用 {FileSize.Format(UsedBytes)}　剩余 {FileSize.Format(Math.Max(0, RemainingBytes))}";

    /// <summary>速度文案。</summary>
    public string RateText => $"↑ {FileSize.Format((long)SentRate)}/s　↓ {FileSize.Format((long)ReceivedRate)}/s";

    private string ScopeText => Scope switch
    {
        TrafficQuotaScope.Upload => "仅上传",
        TrafficQuotaScope.Download => "仅下载",
        _ => "收发合计",
    };
}
