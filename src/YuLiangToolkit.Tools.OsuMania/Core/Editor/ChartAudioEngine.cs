using System;
using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 谱面音频的播放引擎：**设备位置驱动的精确时钟**（编辑器的命脉）。
///
/// <para><b>为什么不用 WPF 的 MediaElement</b>：它的 <c>Position</c> 是「上一次缓冲刷新」的值，
/// 实测精度只有几十毫秒、更新间隔可达 200ms 以上。谱面编辑器里 30ms 的偏差就是「note 和鼓点
/// 明显错开」，完全不能用来对齐节奏。</para>
///
/// <para><b>关键设计</b>：</para>
/// <list type="number">
///   <item><b>时间源是设备字节位置</b>（<see cref="IWavePosition.GetPosition"/>），
///        换算 <c>ms = bytes / AverageBytesPerSecond × 1000</c>；再减掉
///        <see cref="IWavePlayer"/> 报告的输出延迟，得到「此刻耳朵听到的位置」；</item>
///   <item>这个值噪声大（缓冲粒度），所以喂给 <see cref="PlaybackClock"/> 做平滑 + 校准，
///        界面每帧读的是时钟的平滑值，不是设备原始值；</item>
///   <item><b>定位（Seek）</b>：`Stop → 源 CurrentTime = 目标 → 新建输出设备 → Play → 时钟 Start(目标)`。
///        必须重建输出设备，因为 <c>WaveOut</c> 的 <c>GetPosition</c> 在 Stop 之后会归零，
///        复用旧设备会让设备位置和源位置永久错开（实测过）。</item>
///   <item><b>OGG</b> 走 NAudio.Vorbis（NVorbis 纯托管解码）；其它格式（mp3 / wav / flac / m4a）
///        交给 NAudio 的 <see cref="AudioFileReader"/>（内部走 Media Foundation / ACM）。</item>
/// </list>
///
/// <para><b>线程</b>：所有公开方法只允许在 UI 线程调用（<c>WaveOut</c> 的回调在别的线程，但它只做
/// 状态标记，不碰这里的状态）。<see cref="Update"/> 由界面定时器每帧调一次（约 8~16ms）。</para>
///
/// <para><b>无头可测</b>：<see cref="PlaybackClock"/> 的时间源可注入，自检能不起声卡就验证时钟逻辑。</para>
/// </summary>
public sealed class ChartAudioEngine : IDisposable
{
    /// <summary>输出缓冲长度（毫秒）。25ms × 3 个缓冲 ≈ 75ms 延迟，够低又不至于爆音。</summary>
    private const int BufferMilliseconds = 25;

    private const int BufferCount = 3;

    /// <summary>
    /// 缓冲深度里「已经被驱动拿走、但还没真正出声」的比例。实测 25ms × 3 缓冲下
    /// GetPosition 落后真实播放位置约 64ms ≈ 75ms × 0.85。改缓冲参数时这个系数要重新实测。
    /// （只用于 <see cref="OutputLatencyMs"/> 显示，不参与时钟校正。）
    /// </summary>
    private const double LatencyFactor = 0.85;

    private readonly PlaybackClock _clock;

    private IWavePlayer? _output;
    private WaveStream? _reader;
    private ISampleProvider? _samples;

    private double _durationMs;
    private double _volume = 0.8;
    private bool _disposed;

    public ChartAudioEngine()
    {
        _clock = new PlaybackClock();
    }

    /// <summary>注入假时钟的构造（自检用）。</summary>
    public ChartAudioEngine(Func<double> nowMs)
    {
        _clock = new PlaybackClock(nowMs);
    }

    /// <summary>音频文件是不是已经打开（没打开时播放命令应该禁用）。</summary>
    public bool IsLoaded => _reader is not null;

    /// <summary>正在播放。</summary>
    public bool IsPlaying => _clock.IsRunning && IsLoaded;

    /// <summary>当前播放位置（毫秒，平滑后的权威值；已叠加用户的音频偏移）。</summary>
    public double PositionMs => _clock.NowMs + _userOffsetMs;

    /// <summary>音频总时长（毫秒；没加载时为 0）。</summary>
    public double DurationMs => _durationMs;

    /// <summary>
    /// 输出延迟：<c>GetPosition</c> 的读数比真实播放位置落后多少（毫秒）。
    /// **只用于界面显示 / 调试，不参与时钟校正**（读数本身已落后，见 <see cref="Update"/> 的说明）。
    /// 实测 25ms × 3 缓冲时约 64ms。
    /// </summary>
    public double OutputLatencyMs { get; private set; }

    private double _userOffsetMs;

    /// <summary>
    /// 用户音频偏移（毫秒）。正数 = 播放头往后推（音频听起来偏晚时填正数）。
    /// 每条声卡链路的缓冲延迟都不一样，这个值让用户用耳朵对齐。
    /// </summary>
    public double UserOffsetMs
    {
        get => _userOffsetMs;
        set => _userOffsetMs = double.IsFinite(value) ? value : 0;
    }

    /// <summary>上一次时钟校准的漂移（毫秒）。</summary>
    public double LastDriftMs => _clock.LastDriftMs;

    /// <summary>音量 0~1。</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_output is not null) _output.Volume = (float)_volume;
        }
    }

    /// <summary>当前音频文件路径（没加载为空串）。</summary>
    public string AudioPath { get; private set; } = "";

    /// <summary>最近一次失败原因（中文；成功时为空串）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>放完了（在 UI 线程触发 —— 由 <see cref="Update"/> 检查，不在设备回调里抛）。</summary>
    public event EventHandler? PlaybackEnded;

    private bool _endReported;

    /// <summary>
    /// 打开音频文件。支持的扩展名见 <see cref="SupportedExtensions"/>；失败返回 false 并写
    /// <see cref="LastError"/>（例如 OGG 解码器缺失 / 文件损坏）。
    /// </summary>
    public bool Load(string? path)
    {
        ThrowIfDisposed();
        Close();

        LastError = "";
        AudioPath = "";

        if (string.IsNullOrWhiteSpace(path))
        {
            LastError = "没有指定音频文件。";
            return false;
        }

        if (!File.Exists(path))
        {
            LastError = $"音频文件不存在：{path}";
            return false;
        }

        try
        {
            _reader = OpenReader(path);
            _samples = ToSampleProvider(_reader);
            _durationMs = _reader.TotalTime.TotalMilliseconds;
            AudioPath = path;
            _endReported = false;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"音频打开失败（{ex.GetType().Name}）：{ex.Message}";
            Close();
            return false;
        }
    }

    /// <summary>按扩展名挑解码器：OGG 走 NVorbis，其余走 NAudio 的通用读取器。</summary>
    private static WaveStream OpenReader(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            return new VorbisWaveReader(path);
        }

        return new AudioFileReader(path);
    }

    /// <summary>统一成 32 位浮点采样（<see cref="PlaybackClock"/> 与音量调整都按采样算）。</summary>
    private static ISampleProvider ToSampleProvider(WaveStream stream)
    {
        if (stream is AudioFileReader audio) return audio;       // 已经是 float
        return stream.ToSampleProvider();                        // VorbisWaveReader：16 位 → float
    }

    /// <summary>开始 / 继续播放。</summary>
    public bool Play()
    {
        ThrowIfDisposed();
        if (_reader is null || _samples is null) return false;

        if (_clock.IsRunning) return true;

        // 已经放到结尾：从头来（避免“点了没反应”）
        if (_clock.NowMs >= _durationMs - 1)
        {
            return Seek(0) && Play();
        }

        try
        {
            EnsureOutput();
            _output!.Play();

            // 时钟从「源起点」开始走：设备位置已经包含了输出延迟（实测 GetPosition 从 Play 起
            // 就恒定落后真实播放位置 64ms，见 Update 里的说明），所以这里不需要再补静默期。
            _clock.Start(_seekOriginMs);

            _endReported = false;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"播放失败（{ex.GetType().Name}）：{ex.Message}";
            return false;
        }
    }

    /// <summary>暂停（位置保留）。</summary>
    public void Pause()
    {
        if (_disposed) return;
        _clock.Pause();
        try { _output?.Pause(); } catch { /* 设备已失效不影响状态 */ }
    }

    /// <summary>
    /// 定位到指定毫秒。播放中 → 定位后继续播；暂停中 → 只定位。
    ///
    /// <para>⚠ 每次都重建输出设备：<c>WaveOut.GetPosition()</c> 在 Stop 之后归零，
    /// 复用旧设备会让「设备位置」和「源位置」永久错开。</para>
    /// </summary>
    public bool Seek(double positionMs)
    {
        ThrowIfDisposed();
        if (_reader is null) return false;

        var wasPlaying = _clock.IsRunning;
        var target = Math.Clamp(positionMs, 0, Math.Max(0, _durationMs));

        try
        {
            _clock.Pause();
            DisposeOutput();

            _reader.CurrentTime = TimeSpan.FromMilliseconds(target);
            _clock.Seek(target);
            OutputLatencyMs = 0;
            _endReported = false;

            if (wasPlaying)
            {
                EnsureOutput();
                _output!.Play();
                _clock.Start(target);
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = $"定位失败（{ex.GetType().Name}）：{ex.Message}";
            return false;
        }
    }

    /// <summary>停止（位置归零、设备释放）。</summary>
    public void Stop()
    {
        if (_disposed) return;
        _clock.Stop();
        DisposeOutput();
        try
        {
            if (_reader is not null) _reader.CurrentTime = TimeSpan.Zero;
        }
        catch { /* 个别格式不支持 seek 到 0，忽略 */ }
        _endReported = false;
    }

    /// <summary>
    /// 每帧调用（UI 定时器）：① 用设备位置校准平滑时钟；② 检查是否放完。
    /// </summary>
    public void Update()
    {
        if (_disposed || _reader is null) return;

        if (_clock.IsRunning && _output is not null)
        {
            try
            {
                // 输出延迟估计：用**固定的缓冲深度**，不用 CurrentLatency。
                // 实测 CurrentLatency 在 44~72ms 之间乱跳（驱动按块更新），拿它做校正会让播放头跟着抖；
                // 而「设备位置比真实位置落后多少」是恒定的，约等于缓冲队列深度。
                // 取 25ms × 3 缓冲 × 0.8 ≈ 60ms（实测稳态正好 60ms）。
                OutputLatencyMs = BufferMilliseconds * BufferCount * LatencyFactor;

                if (_output is IWavePosition position && _output.OutputWaveFormat is { } format &&
                    format.AverageBytesPerSecond > 0)
                {
                    var playedBytes = position.GetPosition();
                    var playedMs = playedBytes * 1000.0 / format.AverageBytesPerSecond;

                    // 听到的位置 = 源起点 + 设备已播字节。
                    //
                    // ⚠ 关于输出延迟（真机两轮标定，改之前务必按注释里的方法重测）：
                    // waveOut 的 GetPosition 报的是「已经交给驱动的字节」，它从 Play 起就恒定
                    // 落后**真实时间**约 64ms（25ms × 3 缓冲的驱动缓冲深度）。这 64ms 是缓冲里
                    // 还没出声的部分 —— 也就是说：耳朵此刻听到的内容，正好对应 GetPosition 报的这个位置。
                    // 所以这个读数**就是「此刻听到的位置」**，不用再减任何延迟：
                    //   · 只加不减：clock ≈ real − 64ms，但和耳朵听到的内容**完全一致**（这才是要的）
                    //   · 再减一次：clock ≈ real − 128ms，画面比声音还早，音符看起来提前落下
                    // 用户偏移（UserOffsetMs）在 PositionMs 上叠加，不走这里 —— 否则小偏移会被
                    // 死区吃掉，改了没反应。
                    var heardMs = _seekOriginMs + playedMs;
                    if (heardMs >= 0) _clock.Sync(heardMs);
                }
                else
                {
                    // 设备不支持位置查询（很少见）：只靠单调时钟推，至少不会卡死
                    _clock.Sync(_clock.NowMs);
                }
            }
            catch
            {
                // 设备拔掉 / 驱动重启：不要因为一次读位置失败就中断播放
            }
        }

        // 放完了？时钟超过时长（设备可能已经静音）→ 停一次并通知
        if (_clock.IsRunning && _durationMs > 0 && _clock.NowMs >= _durationMs && !_endReported)
        {
            _endReported = true;
            _clock.Pause();
            DisposeOutput();
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 设备位置的「源起点」：每次 Seek / Play 后，设备字节位置从 0 开始数，
    /// 所以听到的时间 = 这个起点 + 设备已播时长 − 缓冲延迟。
    /// </summary>
    private double _seekOriginMs;

    /// <summary>创建输出设备（按需）。</summary>
    private void EnsureOutput()
    {
        if (_output is not null || _samples is null) return;

        var output = new WaveOut
        {
            BufferMilliseconds = BufferMilliseconds,
            NumberOfBuffers = BufferCount,
            Volume = (float)_volume,
        };

        output.Init(_samples);
        _output = output;
        _seekOriginMs = _reader is null ? 0 : _reader.CurrentTime.TotalMilliseconds;

        // 一开始就把延迟估计设好：否则第一次 Sync 会拿 0 去校准，播放头会先往前跳一下
        OutputLatencyMs = BufferMilliseconds * BufferCount * LatencyFactor;
    }

    private void DisposeOutput()
    {
        var output = _output;
        _output = null;
        if (output is null) return;

        try { output.Stop(); } catch { /* 已停止 / 设备没了 */ }
        try { output.Dispose(); } catch { /* 同上 */ }
    }

    /// <summary>彻底放开（切走工具 / 换音频时调）。</summary>
    public void Close()
    {
        if (_disposed) return;

        _clock.Stop();
        DisposeOutput();

        try { _reader?.Dispose(); } catch { /* 忽略 */ }
        _reader = null;
        _samples = null;
        _durationMs = 0;
        _seekOriginMs = 0;
        AudioPath = "";
        _endReported = false;
    }

    /// <summary>可播放的扩展名（界面上的文件对话框过滤器用它）。</summary>
    public static readonly string[] SupportedExtensions =
    {
        ".mp3", ".ogg", ".wav", ".flac", ".m4a", ".aac", ".wma",
    };

    /// <summary>文件对话框过滤器。</summary>
    public static readonly string OpenFileFilter =
        "音频文件 (*.mp3;*.ogg;*.wav;*.flac;*.m4a;*.aac;*.wma)|*.mp3;*.ogg;*.wav;*.flac;*.m4a;*.aac;*.wma|所有文件 (*.*)|*.*";

    /// <summary>扩展名是否受支持（拖放过滤用）。</summary>
    public static bool IsSupportedExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        foreach (var supported in SupportedExtensions)
        {
            if (ext.Equals(supported, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChartAudioEngine));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        GC.SuppressFinalize(this);
    }
}
