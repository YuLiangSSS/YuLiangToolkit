using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace YuLiangToolkit.Tools.Vsrg.Audio;

/// <summary>
/// 游玩音频引擎：**设备位置驱动的精确时钟**，喂给 <see cref="PlaybackClock"/>。
///
/// <para><b>关键标定（改之前务必读）</b>：<c>waveOut</c> 的
/// <see cref="IWavePosition.GetPosition"/> 报的读数**就是「此刻耳朵听到的位置」**
/// （它从 Play 起就恒定落后真实时间一个缓冲深度，也就是缓冲里还没出声的那部分）。
/// 所以设备位置**直接**喂给时钟，**不要再减任何延迟** —— 减了画面会比声音早，
/// 看起来像「音符提前落下」。这一点已经在同项目的谱面编辑器上真机标定过两轮
/// （见 <c>Tools.OsuMania/Core/Editor/ChartAudioEngine.cs</c> 的 Update 注释）。</para>
///
/// <para><b>用户偏移</b>叠加在「给出的播放位置」上（<see cref="PositionMs"/>），
/// 不参与时钟内部校准 —— 否则小偏移会被时钟的死区吃掉，改了没反应。</para>
///
/// <para><b>线程</b>：公开方法只允许在 UI 线程调；<see cref="Update"/> 由界面定时器每帧调一次。</para>
/// </summary>
public sealed class AudioClockEngine : IDisposable
{
    /// <summary>输出缓冲长度（毫秒）。25ms × 3 个缓冲 ≈ 75ms 的队列深度，够低又不至于爆音。</summary>
    private const int BufferMilliseconds = 25;

    private const int BufferCount = 3;

    private readonly PlaybackClock _clock;

    private IWavePlayer? _output;
    private WaveStream? _reader;
    private ISampleProvider? _samples;

    private double _durationMs;
    private double _seekOriginMs;
    private double _volume = 0.85;
    private bool _disposed;
    private bool _endReported;

    /// <summary>默认构造（用单调时钟）。</summary>
    public AudioClockEngine()
    {
        _clock = new PlaybackClock();
    }

    /// <summary>注入时间源（自检用）。</summary>
    public AudioClockEngine(Func<double> nowMs)
    {
        _clock = new PlaybackClock(nowMs);
    }

    /// <summary>音频文件有没有打开（没打开时「开始」按钮应该禁用）。</summary>
    public bool IsLoaded => _reader is not null;

    /// <summary>正在播放。</summary>
    public bool IsPlaying => _clock.IsRunning && IsLoaded;

    /// <summary>音频文件路径（没加载为空串）。</summary>
    public string AudioPath { get; private set; } = "";

    /// <summary>音频总时长（毫秒；没加载为 0）。</summary>
    public double DurationMs => _durationMs;

    /// <summary>最近一次失败原因（中文；成功时为空串）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>播放位置（毫秒，已叠加用户音频偏移；这是游玩引擎该用的时间）。</summary>
    public double PositionMs => _clock.NowMs + _userOffsetMs;

    private double _userOffsetMs;

    /// <summary>
    /// 用户音频偏移（毫秒）：正数 = 判定时间整体往后挪（音乐听起来偏晚时填正数）。
    /// 只叠加在 <see cref="PositionMs"/> 上，不参与时钟内部校准（见类注释）。
    /// </summary>
    public double UserOffsetMs
    {
        get => _userOffsetMs;
        set => _userOffsetMs = double.IsFinite(value) ? value : 0;
    }

    /// <summary>音量（0~1）。</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(double.IsFinite(value) ? value : 1, 0, 1);
            if (_output is not null) _output.Volume = (float)_volume;
        }
    }

    /// <summary>放完了（在 UI 线程触发：由 <see cref="Update"/> 检查，不在设备回调里抛）。</summary>
    public event EventHandler? PlaybackEnded;

    /// <summary>音频是不是已经放到结尾（放完只停音频，**不代表这一局结束** —— 谱面可能还有 note）。</summary>
    public bool IsEnded { get; private set; }

    /// <summary>打开音频文件。失败返回 false 并写 <see cref="LastError"/>。</summary>
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

    /// <summary>按扩展名挑解码器：OGG / OPUS 走 NAudio.Vorbis 之外的路，其余走通用读取器。</summary>
    private static WaveStream OpenReader(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            return new VorbisWaveReader(path);
        }

        return new AudioFileReader(path);
    }

    /// <summary>统一成 32 位浮点采样（音量调整与设备初始化都按采样算）。</summary>
    private static ISampleProvider ToSampleProvider(WaveStream stream)
    {
        if (stream is AudioFileReader audio) return audio;   // 已经是 float
        return stream.ToSampleProvider();                    // VorbisWaveReader：16 位 → float
    }

    /// <summary>从 0 开始播（每次开一局都从头放）。</summary>
    public bool PlayFromStart()
    {
        ThrowIfDisposed();
        if (_reader is null || _samples is null) return false;

        try
        {
            _clock.Pause();
            DisposeOutput();

            _reader.CurrentTime = TimeSpan.Zero;
            _seekOriginMs = 0;
            _clock.Seek(0);
            _endReported = false;
            IsEnded = false;

            EnsureOutput();
            _output!.Play();
            _clock.Start(0);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"播放失败（{ex.GetType().Name}）：{ex.Message}";
            return false;
        }
    }

    /// <summary>暂停（位置保留，可以再 <see cref="Resume"/>）。</summary>
    public void Pause()
    {
        if (_disposed) return;
        _clock.Pause();
        try { _output?.Pause(); } catch { /* 设备已失效不影响状态 */ }
    }

    /// <summary>从暂停处继续。</summary>
    public bool Resume()
    {
        ThrowIfDisposed();
        if (_reader is null) return false;
        if (_clock.IsRunning) return true;

        try
        {
            EnsureOutput();
            _output!.Play();
            _clock.Resume();
            _endReported = false;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"继续播放失败（{ex.GetType().Name}）：{ex.Message}";
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
        _seekOriginMs = 0;
        _endReported = false;
        IsEnded = false;
    }

    /// <summary>
    /// 每帧调用（UI 定时器）：① 用设备位置校准平滑时钟；② 检查是不是放完了。
    /// </summary>
    public void Update()
    {
        if (_disposed || _reader is null) return;

        if (_clock.IsRunning && _output is not null)
        {
            try
            {
                if (_output is IWavePosition position && _output.OutputWaveFormat is { } format &&
                    format.AverageBytesPerSecond > 0)
                {
                    var playedBytes = position.GetPosition();
                    var playedMs = playedBytes * 1000.0 / format.AverageBytesPerSecond;

                    // ⚠ 这个读数就是「此刻听到的位置」，不要再减缓冲延迟（见类注释的标定说明）
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

        if (_clock.IsRunning && _durationMs > 0 && _clock.NowMs >= _durationMs && !_endReported)
        {
            _endReported = true;
            IsEnded = true;

            // ⚠ 只停音频，**不停时钟**：谱面可能比音频长（剪短的音频 / 音频头被裁过），
            // 停表会让后面的 note 永远等不到、这一局卡死在游玩页。
            // 时钟继续按单调时钟往前走，判定照常推进，这一局由引擎自己判断「打完」。
            DisposeOutput();
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

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
    }

    private void DisposeOutput()
    {
        var output = _output;
        _output = null;
        if (output is null) return;

        try { output.Stop(); } catch { /* 已停止 / 设备没了 */ }
        try { output.Dispose(); } catch { /* 同上 */ }
    }

    /// <summary>彻底放开（切走工具 / 退出游玩时调）。</summary>
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
        IsEnded = false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioClockEngine));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        GC.SuppressFinalize(this);
    }
}
