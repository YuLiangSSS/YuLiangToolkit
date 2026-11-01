using YuLiangToolkit.Tools.Live2D.Audio.Analysis;

namespace YuLiangToolkit.Tools.Live2D.Audio;

/// <summary>
/// 音乐跟唱 / 律动的数据源：<see cref="WasapiLoopbackCapture"/> 采集系统正在播放的声音 →
/// <see cref="MusicAnalyzer"/> 做 FFT / YIN / 共振峰 / 立体声中心声道 / 音节调制 / 节拍分析 →
/// 发布 <see cref="MusicAnalysisSnapshot"/> 给运行时与界面。
///
/// <para><b>隐私</b>：只做实时特征提取，**不录音、不写任何音频文件**，也不申请麦克风；
/// 采集的是扬声器混音（回环），关掉开关就立刻释放设备。</para>
///
/// <para><b>线程</b>：采集线程里同步调用分析器（每帧目标 &lt; 1.5ms），
/// 分析结果以不可变快照发布，UI / 运行时只读。</para>
/// </summary>
public sealed class MusicReactiveSource : IMusicReactiveProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WasapiLoopbackCapture? _capture;
    private MusicAnalyzer? _analyzer;
    private SmtcWatcher? _smtc;
    private volatile MusicAnalysisSnapshot? _latest;
    private volatile bool _running;
    private string? _lastError;
    private double _sensitivity = 1.0;
    private int _restartRequested;

    /// <summary>采集 / 分析出错（停止后触发；后台线程）。</summary>
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public MusicAnalysisSnapshot? Latest => _latest;

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public string? LastError => _lastError;

    /// <inheritdoc />
    public string MediaTitle => _smtc?.Title ?? "";

    /// <inheritdoc />
    public string MediaArtist => _smtc?.Artist ?? "";

    /// <inheritdoc />
    public string MediaApp => _smtc?.App ?? "";

    /// <summary>
    /// 识别灵敏度（0.3 ~ 3.0，越大越容易把歌判成「带歌词」）。
    /// 可以直接在线改，不需要重启采集。
    /// </summary>
    public double Sensitivity
    {
        get => _sensitivity;
        set
        {
            _sensitivity = Math.Clamp(value, 0.3, 3.0);
            var analyzer = _analyzer;
            if (analyzer is not null) analyzer.Sensitivity = _sensitivity;
        }
    }

    /// <summary>启动系统音频回环采集 + 分析（失败返回 false，<see cref="LastError"/> 有原因）。</summary>
    public async Task<bool> StartAsync()
    {
        return await Task.Run(Start).ConfigureAwait(false);
    }

    private bool Start()
    {
        _gate.Wait();
        try
        {
            StopCore();
            _lastError = null;

            var capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnDataAvailable;
            capture.DeviceChanged += OnDeviceChanged;
            capture.Failed += OnCaptureFailed;
            if (!capture.Start())
            {
                _lastError = capture.LastError ?? "启动系统音频回环失败";
                capture.Dispose();
                _latest = null;
                StateChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

            _capture = capture;
            _analyzer = new MusicAnalyzer(capture.SampleRate) { Sensitivity = _sensitivity };

            // SMTC 只是显示歌名/歌手用，失败不影响音频分析
            _smtc = new SmtcWatcher();
            _smtc.Start();
            _smtc.Changed += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);

            _running = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _lastError = "启动音乐分析失败：" + ex.Message;
            StopCore();
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnDataAvailable(float[] stereoSamples, int frameCount)
    {
        var analyzer = _analyzer;
        if (analyzer is null || frameCount <= 0) return;
        try
        {
            _latest = analyzer.Analyze(stereoSamples.AsSpan(0, frameCount * 2), frameCount, Environment.TickCount64);
        }
        catch (Exception ex)
        {
            _lastError = "音频分析失败：" + ex.Message;
            Interlocked.Exchange(ref _restartRequested, 1);
        }
    }

    private void OnCaptureFailed(object? sender, string message)
    {
        _lastError = message;
        _running = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDeviceChanged(object? sender, EventArgs e)
    {
        // 默认播放设备变了（切蓝牙 / 拔耳机）：重启采集，分析器重置（新设备、新内容）
        if (Interlocked.Exchange(ref _restartRequested, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                Thread.Sleep(300);
                try { RestartCapture(); } catch { }
            });
        }
    }

    private void RestartCapture()
    {
        _gate.Wait();
        try
        {
            if (!_running) return;
            StopCore(keepRunning: true);
            var capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnDataAvailable;
            capture.DeviceChanged += OnDeviceChanged;
            capture.Failed += OnCaptureFailed;
            if (capture.Start())
            {
                _capture = capture;
                _analyzer = new MusicAnalyzer(capture.SampleRate) { Sensitivity = _sensitivity };
            }
            else
            {
                capture.Dispose();
                _lastError = "切换播放设备后重启采集失败：" + (capture.LastError ?? "未知原因");
                _running = false;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _restartRequested, 0);
            _gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>停止采集并释放设备（可重复调用）。</summary>
    public async Task StopAsync()
    {
        await Task.Run(() =>
        {
            _gate.Wait();
            try
            {
                StopCore();
            }
            finally
            {
                _gate.Release();
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(false);
    }

    private void StopCore(bool keepRunning = false)
    {
        _running = false;
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            try { capture.DataAvailable -= OnDataAvailable; } catch { }
            try { capture.DeviceChanged -= OnDeviceChanged; } catch { }
            try { capture.Failed -= OnCaptureFailed; } catch { }
            try { capture.Stop(); } catch { }
            try { capture.Dispose(); } catch { }
        }

        var smtc = _smtc;
        _smtc = null;
        try { smtc?.Stop(); } catch { }
        try { smtc?.Dispose(); } catch { }

        _analyzer = null;
        _latest = null;
        if (!keepRunning) _lastError = null;
    }

    public void Dispose()
    {
        try
        {
            _gate.Wait();
            try { StopCore(); } finally { _gate.Release(); }
        }
        catch
        {
            // 退出阶段忽略
        }
        _gate.Dispose();
    }
}
