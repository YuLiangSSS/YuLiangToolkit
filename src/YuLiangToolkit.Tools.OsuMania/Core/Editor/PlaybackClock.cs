using System;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// **播放时钟**：把「音频设备的播放位置」和「单调时钟」融合成一条平滑、精确、可渲染的时间轴。
///
/// <para><b>为什么不能直接用音频设备位置</b>：<c>waveOutGetPosition</c> 之类的设备位置是
/// 「已交给驱动 / 已消费」的字节数，粒度粗（实测 25ms 缓冲下抖动 ±30ms），而且回调时机不固定。
/// 直接把它当播放头 → 谱面线每帧乱跳、note 和音乐对不上。</para>
///
/// <para><b>为什么不能只用单调时钟（Stopwatch）</b>：声卡时钟和 CPU 时钟不同源，几分钟下来会漂
/// 几十毫秒；用户拖动进度条 / 系统卡一下之后更会彻底跑偏。</para>
///
/// <para><b>本类的做法</b>（教科书式的媒体时钟）：</para>
/// <list type="number">
///   <item>以「锚点」为准：<c>Pos = AnchorPos + (Now − AnchorNow) × Rate</c>，靠单调时钟平滑推进；</item>
///   <item>定时拿设备位置校准：算 <c>drift = 设备位置 − 推算位置</c>；</item>
///   <item>小漂移（&lt; <see cref="SoftDriftMs"/>）→ 只微调 <c>Rate</c>（±<see cref="MaxRateAdjust"/>），
///        让播放头**平滑**地追上去，绝不跳；</item>
///   <item>大漂移（&gt; <see cref="HardResyncMs"/>）→ 说明发生了定位 / 卡顿 / 设备重建，直接硬对齐锚点。</item>
/// </list>
///
/// <para>结果是：画面上的播放头按「肉眼绝对平滑」的速度走，同时长期不会和声音分离。</para>
///
/// <para><b>可测试性</b>：时间源是一个 <c>Func&lt;double&gt;</c>，自检注入假时钟就能把
/// 「播放 / 暂停 / 定位 / 漂移校正 / 硬重同步」整条链路跑一遍，不需要真的开声卡。</para>
/// </summary>
public sealed class PlaybackClock
{
    /// <summary>漂移小于这个值（毫秒）就当噪声忽略，不校正。</summary>
    public const double DeadZoneMs = 2.0;

    /// <summary>小于这个漂移（毫秒）只做速率微调（平滑追赶）。</summary>
    public const double SoftDriftMs = 120.0;

    /// <summary>大于这个漂移（毫秒）直接硬对齐（定位 / 卡顿 / 设备重建）。</summary>
    public const double HardResyncMs = 120.0;

    /// <summary>速率微调的最小幅度（±<b>4%</b>）。</summary>
    /// <remarks>
    /// 为什么要给到 4%：声卡晶振和 CPU 时钟本身就差 ~1%（实测 4 秒就累积 50ms 滞后），
    /// 而追 50ms 漂移需要 <c>50ms / rate差</c> 秒。1.5% 要 3.3 秒才追得上，导致播放头长期落后，
    /// 稳定态误差 50ms —— 对音游对齐来说是能听出来的。4% 时约 1.2 秒追上；
    /// 这个幅度只持续一两秒、且是缓慢变化，肉眼看不出变速（比起来回震荡要轻微得多）。
    /// </remarks>
    public const double MaxRateAdjust = 0.04;

    /// <summary>一次校正最多吃掉漂移的这个比例（避免过冲）。</summary>
    public const double CorrectionGain = 0.02;

    private readonly Func<double> _nowMs;

    private double _anchorPositionMs;
    private double _anchorNowMs;
    private double _rate = 1.0;
    private bool _running;

    /// <summary>用单调时钟（自检可注入假时钟）。</summary>
    public PlaybackClock(Func<double> nowMs)
    {
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
    }

    /// <summary>默认用 <see cref="System.Diagnostics.Stopwatch"/> 的高精度计时（进程级单调时钟）。</summary>
    public PlaybackClock() : this(DefaultNow)
    {
    }

    private static readonly System.Diagnostics.Stopwatch SharedWatch = System.Diagnostics.Stopwatch.StartNew();

    private static double DefaultNow() => SharedWatch.Elapsed.TotalMilliseconds;

    /// <summary>是否正在推进（暂停 / 停止时为 false，此时 <see cref="NowMs"/> 保持不动）。</summary>
    public bool IsRunning => _running;

    /// <summary>当前播放位置（毫秒）。暂停时就是暂停那一刻的位置。</summary>
    /// <remarks>
    /// 单调时钟理论上不会倒退，但注入的假时钟 / 系统休眠恢复 / 时钟源切换都可能让它往回跳。
    /// 这里记住「走到的最大位置」：倒退时冻结在最大值，绝不往回走 —— 否则播放头会突然往回跳，
    /// 用户会以为谱面错位了。
    /// </remarks>
    public double NowMs
    {
        get
        {
            if (!_running) return _anchorPositionMs;

            var elapsed = _nowMs() - _anchorNowMs;
            if (!double.IsFinite(elapsed) || elapsed < 0) elapsed = 0;

            var position = _anchorPositionMs + elapsed * _rate;
            if (position < _maxPositionMs) return _maxPositionMs;

            _maxPositionMs = position;
            return position;
        }
    }

    /// <summary>本次播放走到过的最大位置（防时钟倒退用）。</summary>
    private double _maxPositionMs;

    /// <summary>当前推进速率（1.0 = 正常；微调期间会略偏离）。</summary>
    public double Rate => _rate;

    /// <summary>上一次 <see cref="Sync"/> 算出的漂移（毫秒；正 = 设备比推算快）。调试 / 自检用。</summary>
    public double LastDriftMs { get; private set; }

    /// <summary>累计硬重同步次数（自检断言 / 调试用）。</summary>
    public int HardResyncCount { get; private set; }

    /// <summary>
    /// 从 <paramref name="positionMs"/> 开始走。**定位后必须调用它**（先 Seek 音频设备，再 Start 时钟）。
    /// </summary>
    public void Start(double positionMs)
    {
        _anchorPositionMs = positionMs;
        _anchorNowMs = _nowMs();
        _rate = 1.0;
        _maxPositionMs = positionMs;
        _running = true;
    }

    /// <summary>接着当前位置继续走（暂停后恢复）。</summary>
    public void Resume()
    {
        if (_running) return;
        _anchorNowMs = _nowMs();
        _rate = 1.0;
        _maxPositionMs = _anchorPositionMs;
        _running = true;
    }

    /// <summary>暂停：把当前位置冻结成新的锚点。</summary>
    public void Pause()
    {
        if (!_running) return;
        _anchorPositionMs = NowMs;
        _anchorNowMs = _nowMs();
        _rate = 1.0;
        _running = false;
    }

    /// <summary>
    /// 跳到某位置（播放中 / 暂停中都可用）。播放中会继续走，暂停中就停在目标位置。
    /// </summary>
    public void Seek(double positionMs)
    {
        _anchorPositionMs = positionMs;
        _anchorNowMs = _nowMs();
        _rate = 1.0;   // 定位后速率立刻归 1：新位置是权威值，不需要追赶
        _maxPositionMs = positionMs;
    }

    /// <summary>停止（位置归零、不再推进）。</summary>
    public void Stop()
    {
        _anchorPositionMs = 0;
        _anchorNowMs = _nowMs();
        _rate = 1.0;
        _maxPositionMs = 0;
        _running = false;
    }

    /// <summary>
    /// 用音频设备位置校准（播放中每隔几十毫秒调一次）。
    ///
    /// <para><paramref name="devicePositionMs"/> 应该是**换算成"已经听到"的时间**
    /// （调用方负责减掉输出延迟）。</para>
    /// </summary>
    public void Sync(double devicePositionMs)
    {
        if (!_running) return;
        if (!double.IsFinite(devicePositionMs)) return;

        var predicted = NowMs;
        var drift = devicePositionMs - predicted;
        LastDriftMs = drift;

        if (Math.Abs(drift) <= DeadZoneMs)
        {
            // 噪声：速率慢慢回正
            _rate = Approach(_rate, 1.0, 0.05);
            return;
        }

        if (Math.Abs(drift) >= HardResyncMs)
        {
            // 定位 / 严重卡顿 / 设备重建：硬对齐（画面会跳一下，但这是正确的位置）
            HardResyncCount++;
            _anchorPositionMs = devicePositionMs;
            _anchorNowMs = _nowMs();
            _rate = 1.0;
            _maxPositionMs = devicePositionMs;
            return;
        }

        // 小漂移：按「希望在多少毫秒内追平」反算需要的速率差（不是拍脑袋的比例控制）。
        // 追平时间取 800ms：漂移 50ms → 速率 +6.25%（会被夹到 MaxRateAdjust），
        // 大约 1~2 秒内追上，然后回到 1.0 附近；肉眼看不出变速。
        var desiredRate = 1.0 + drift / CatchUpMs;
        _rate = Math.Clamp(Approach(_rate, desiredRate, 0.3), 1.0 - MaxRateAdjust, 1.0 + MaxRateAdjust);
    }

    /// <summary>希望在这个时间（毫秒）内把漂移追平。</summary>
    public const double CatchUpMs = 800.0;

    /// <summary>把 <paramref name="current"/> 朝 <paramref name="target"/> 挪 <paramref name="fraction"/> 比例。</summary>
    private static double Approach(double current, double target, double fraction)
        => current + (target - current) * Math.Clamp(fraction, 0, 1);
}
