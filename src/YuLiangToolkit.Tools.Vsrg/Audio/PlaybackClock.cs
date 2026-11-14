using System.Diagnostics;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace YuLiangToolkit.Tools.Vsrg.Audio;

/// <summary>
/// 游玩用的**播放时钟**：把「音频设备的播放位置」和「单调时钟」融合成一条平滑、精确、可渲染的时间轴。
///
/// <para>和 <c>Tools.OsuMania/Core/Editor/PlaybackClock.cs</c> 同一个思路（那边已经用真机标定过），
/// 但在本工具里独立实现：游玩只需要「现在第几毫秒」，不需要编辑器的定位 / 硬重同步那一套，
/// 也避免两个工具互相牵制。</para>
///
/// <para><b>为什么不能直接用设备位置</b>：<c>waveOutGetPosition</c> 的粒度粗（25ms 缓冲下抖动 ±30ms），
/// 直接当播放头会让 note 每帧乱跳。</para>
///
/// <para><b>为什么不能只用单调时钟</b>：声卡时钟和 CPU 时钟不同源，几分钟下来会漂几十毫秒。</para>
///
/// <para><b>可测试性</b>：时间源是注入的 <c>Func&lt;double&gt;</c>，自检能不起声卡就把
/// 「推进 / 暂停 / 校准 / 截止」整条链路跑一遍。</para>
/// </summary>
public sealed class PlaybackClock
{
    /// <summary>漂移小于这个值（毫秒）当噪声忽略。</summary>
    public const double DeadZoneMs = 2.0;

    /// <summary>大于这个漂移（毫秒）直接硬对齐（发生了卡顿 / 设备重建）。</summary>
    public const double HardResyncMs = 150.0;

    /// <summary>速率微调上限（±4%）：足够在 1~2 秒内追上几十毫秒的漂移，肉眼看不出变速。</summary>
    public const double MaxRateAdjust = 0.04;

    /// <summary>希望在这个时间（毫秒）内把漂移追平。</summary>
    public const double CatchUpMs = 800.0;

    private readonly Func<double> _nowMs;

    private double _anchorPositionMs;
    private double _anchorNowMs;
    private double _rate = 1.0;
    private double _maxPositionMs;
    private bool _running;

    /// <summary>用单调时钟（默认 <see cref="Stopwatch"/>）。</summary>
    public PlaybackClock() : this(DefaultNow)
    {
    }

    /// <summary>注入时间源（自检用：给一个能自己拨的假时钟）。</summary>
    public PlaybackClock(Func<double> nowMs)
    {
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
    }

    private static readonly Stopwatch SharedWatch = Stopwatch.StartNew();

    private static double DefaultNow() => SharedWatch.Elapsed.TotalMilliseconds;

    /// <summary>是否正在推进。</summary>
    public bool IsRunning => _running;

    /// <summary>当前播放位置（毫秒）。暂停时就是暂停那一刻的位置。</summary>
    /// <remarks>单调时钟理论上不会倒退，但系统休眠恢复 / 时钟源切换会；这里冻结在最大值，绝不往回跳。</remarks>
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

    /// <summary>当前推进速率（正常为 1.0）。</summary>
    public double Rate => _rate;

    /// <summary>上一次校准的漂移（毫秒）。</summary>
    public double LastDriftMs { get; private set; }

    /// <summary>累计硬重同步次数。</summary>
    public int HardResyncCount { get; private set; }

    /// <summary>从 <paramref name="positionMs"/> 开始走。</summary>
    public void Start(double positionMs)
    {
        _anchorPositionMs = positionMs;
        _anchorNowMs = _nowMs();
        _rate = 1.0;
        _maxPositionMs = positionMs;
        _running = true;
    }

    /// <summary>暂停（冻结当前位置）。</summary>
    public void Pause()
    {
        if (!_running) return;
        _anchorPositionMs = NowMs;
        _anchorNowMs = _nowMs();
        _rate = 1.0;
        _running = false;
    }

    /// <summary>从当前位置继续。</summary>
    public void Resume()
    {
        if (_running) return;
        _anchorNowMs = _nowMs();
        _rate = 1.0;
        _maxPositionMs = _anchorPositionMs;
        _running = true;
    }

    /// <summary>跳到某个位置（播放中 / 暂停中都可用）。</summary>
    public void Seek(double positionMs)
    {
        _anchorPositionMs = positionMs;
        _anchorNowMs = _nowMs();
        _rate = 1.0;
        _maxPositionMs = positionMs;
    }

    /// <summary>停下并归零。</summary>
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
    /// <paramref name="devicePositionMs"/> 要传「已经听到的位置」（已经换算成毫秒）。
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
            _rate = Approach(_rate, 1.0, 0.05);
            return;
        }

        if (Math.Abs(drift) >= HardResyncMs)
        {
            HardResyncCount++;
            _anchorPositionMs = devicePositionMs;
            _anchorNowMs = _nowMs();
            _rate = 1.0;
            _maxPositionMs = devicePositionMs;
            return;
        }

        // 小漂移：按「希望在 CatchUpMs 内追平」反算需要的速率差
        var desiredRate = 1.0 + drift / CatchUpMs;
        _rate = Math.Clamp(Approach(_rate, desiredRate, 0.3), 1.0 - MaxRateAdjust, 1.0 + MaxRateAdjust);
    }

    private static double Approach(double current, double target, double fraction)
        => current + (target - current) * Math.Clamp(fraction, 0, 1);
}
