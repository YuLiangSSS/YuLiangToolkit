using YuLiangToolkit.Tools.ChatRoom.Call;

namespace YuLiangToolkit.FrameworkSelfCheck;

// ============================================================================
// 通话自检用的「假设备」：
//   · 假摄像头 —— 后台 Task 按 ~10 fps 产出一帧 160×120 的 BGRA 渐变；
//   · 假麦克风 —— 后台 Task 每 40 ms 产出 640 个 16 kHz 单声道正弦样本；
//   · 假扬声器 —— 后台 Task 每 10 ms 向引擎「拉」480 帧立体声（拉模型）；
//   · 假工厂   —— 把上面三个交出去，引擎拿到的全是假设备。
//
// 存在的理由：自检机器上不一定有摄像头 / 麦克风，而且**绝不能**去开真设备
// （会亮摄像头灯、会录到环境声）。引擎只认 CallContracts.cs 里的接口，
// 所以塞一套假实现就能把「采集 → 编码 → 发送 → 中转 → 接收 → 混音 → 播放」
// 整条链路无头跑一遍。所有 Stop / Dispose 都是幂等的。
// ============================================================================

/// <summary>画假画面用的小工具（BGRA32、行优先、自上而下）。</summary>
internal static class FakeFramePainter
{
    /// <summary>
    /// 造一帧「像素级渐变」的 BGRA 数据；<paramref name="phase"/> 每帧变一下，
    /// 这样连续几帧的 JPEG 不会完全一样（更接近真实采集）。
    /// </summary>
    public static byte[] GradientBgra(int width, int height, int phase)
    {
        var bytes = new byte[width * height * 4];
        var maxX = Math.Max(1, width - 1);
        var maxY = Math.Max(1, height - 1);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                bytes[i] = (byte)((x * 255 / maxX + phase * 4) & 0xFF);        // B
                bytes[i + 1] = (byte)(y * 255 / maxY);                          // G
                bytes[i + 2] = (byte)((x + y) * 127 / Math.Max(1, width + height - 2)); // R
                bytes[i + 3] = 255;                                             // A
            }
        }

        return bytes;
    }
}

/// <summary>假摄像头：按固定帧率往外面推 BGRA 渐变帧，不碰任何真实设备。</summary>
internal sealed class FakeCameraSource : ICameraSource
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _disposed;
    private long _pushed;

    public FakeCameraSource(int width = 160, int height = 120, int fps = 10)
    {
        _width = Math.Max(2, width);
        _height = Math.Max(2, height);
        _fps = Math.Max(1, fps);
    }

    /// <summary>已经推出去多少帧（自检排错用）。</summary>
    public long FramesPushed => Interlocked.Read(ref _pushed);

    public string? LastError { get; private set; }

    public event Action<CameraFrameData>? FrameArrived;

    public Task<(bool Ok, string Error)> StartAsync(string deviceId)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.FromResult((false, "假摄像头已经释放"));

        lock (_gate)
        {
            if (_loop is not null) return Task.FromResult((true, ""));
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }

        return Task.FromResult((true, ""));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var intervalMs = Math.Max(10, 1000 / _fps);
        var phase = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = new CameraFrameData(
                    FakeFramePainter.GradientBgra(_width, _height, phase), _width, _height, Environment.TickCount64);
                try { FrameArrived?.Invoke(frame); } catch { /* 引擎处理异常不能把采集线程带走 */ }
                Interlocked.Increment(ref _pushed);
                phase = (phase + 5) & 0xFF;
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = ex.Message; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            loop = _loop;
            _loop = null;
            cts = _cts;
            _cts = null;
        }

        try { cts?.Cancel(); } catch { }
        if (loop is not null)
        {
            // 不无限等：引擎里若正好在回调里持锁，这里等太久会把自检拖住（等 1.5 秒足够它退出了）
            try { await Task.WhenAny(loop, Task.Delay(1500)).ConfigureAwait(false); } catch { }
        }
        try { cts?.Dispose(); } catch { }
    }
}

/// <summary>假麦克风：每 40 ms 推一段 16 kHz 单声道正弦（正好是通话引擎要的采样率）。</summary>
internal sealed class FakeMicrophoneSource : IMicrophoneSource
{
    private readonly int _sampleRate;
    private readonly int _intervalMs;
    private readonly int _framesPerPacket;
    private readonly double _frequency;
    private readonly float _amplitude;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _disposed;
    private long _pushed;
    private double _phase;

    public FakeMicrophoneSource(int sampleRate = 16000, int intervalMs = 40, double frequency = 440, float amplitude = 0.25f)
    {
        _sampleRate = Math.Max(1000, sampleRate);
        _intervalMs = Math.Max(5, intervalMs);
        _framesPerPacket = Math.Max(1, _sampleRate * _intervalMs / 1000);
        _frequency = frequency;
        _amplitude = Math.Clamp(amplitude, 0.01f, 0.9f);
    }

    /// <summary>已经推出去多少段（自检排错用）。</summary>
    public long PacketsPushed => Interlocked.Read(ref _pushed);

    public int SampleRate => _sampleRate;

    public string? LastError { get; private set; }

    public event Action<float[], int>? SamplesAvailable;

    public (bool Ok, string Error) Start(string deviceId)
    {
        if (Volatile.Read(ref _disposed) != 0) return (false, "假麦克风已经释放");

        lock (_gate)
        {
            if (_loop is not null) return (true, "");
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }

        return (true, "");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var buffer = new float[_framesPerPacket];
                for (var i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = _amplitude * MathF.Sin((float)(_phase + 2 * Math.PI * _frequency * i / _sampleRate));
                }

                _phase += 2 * Math.PI * _frequency * _framesPerPacket / _sampleRate;
                if (_phase > Math.PI * 2) _phase %= Math.PI * 2;

                try { SamplesAvailable?.Invoke(buffer, buffer.Length); } catch { /* 引擎异常不影响采集线程 */ }
                Interlocked.Increment(ref _pushed);
                await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = ex.Message; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            loop = _loop;
            _loop = null;
            cts = _cts;
            _cts = null;
        }

        try { cts?.Cancel(); } catch { }
        try { loop?.Wait(TimeSpan.FromMilliseconds(1500)); } catch { }
        try { cts?.Dispose(); } catch { }
    }
}

/// <summary>
/// 假扬声器：按「拉」模型每 10 ms 向通话引擎要 480 帧立体声。
/// <see cref="PulledFrames"/> 涨了就说明播放链路（混音 → 拉数据）真的在跑。
/// </summary>
internal sealed class FakeSpeakerSink : IAudioSink
{
    private readonly int _sampleRate;
    private readonly int _framesPerPull;
    private readonly int _intervalMs;
    private readonly object _gate = new();
    private Func<int, float[]>? _pull;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _disposed;
    private long _pulledFrames;
    private long _pullCalls;

    public FakeSpeakerSink(int sampleRate = 48000, int framesPerPull = 480, int intervalMs = 10)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _framesPerPull = Math.Max(16, framesPerPull);
        _intervalMs = Math.Max(5, intervalMs);
    }

    /// <summary>已经从引擎拉走多少帧（每帧 = 2 个 float）。</summary>
    public long PulledFrames => Interlocked.Read(ref _pulledFrames);

    /// <summary>拉了多少次（自检排错用）。</summary>
    public long PullCalls => Interlocked.Read(ref _pullCalls);

    public int SampleRate => _sampleRate;

    public string? LastError { get; private set; }

    public (bool Ok, string Error) Start(string deviceId, Func<int, float[]> pullStereo)
    {
        if (Volatile.Read(ref _disposed) != 0) return (false, "假扬声器已经释放");
        if (pullStereo is null) return (false, "没有提供拉数据的回调");

        lock (_gate)
        {
            _pull = pullStereo;
            if (_loop is not null) return (true, "");
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }

        return (true, "");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pull = _pull;
                if (pull is not null)
                {
                    try
                    {
                        var data = pull(_framesPerPull);
                        // 引擎约定返回 frames × 2 的 interleaved 立体声；长度不够算这一轮失败
                        if (data is not null && data.Length >= _framesPerPull * 2)
                            Interlocked.Add(ref _pulledFrames, _framesPerPull);
                        else
                            LastError = $"pull 返回的数据长度不对（{data?.Length ?? 0}，期望 {_framesPerPull * 2}）";
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                    }
                }

                Interlocked.Increment(ref _pullCalls);
                await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = ex.Message; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            loop = _loop;
            _loop = null;
            cts = _cts;
            _cts = null;
            _pull = null;
        }

        try { cts?.Cancel(); } catch { }
        // 有超时地等：避免「引擎在 pull 回调里持锁 → 这里同步等」时把自检挂死
        try { loop?.Wait(TimeSpan.FromMilliseconds(1500)); } catch { }
        try { cts?.Dispose(); } catch { }
    }
}

/// <summary>
/// 假媒体工厂：把上面的假摄像头 / 麦克风 / 扬声器交给通话引擎。
/// 自检结束时调 <see cref="DisposeCreatedAsync"/> 兜底收掉还没释放的设备线程。
/// </summary>
internal sealed class FakeCallMediaFactory : ICallMediaFactory
{
    private static readonly IReadOnlyList<CallDevice> Cameras = new[] { new CallDevice("fake-camera", "自检假摄像头") };
    private static readonly IReadOnlyList<CallDevice> Microphones = new[] { new CallDevice("fake-microphone", "自检假麦克风") };
    private static readonly IReadOnlyList<CallDevice> Speakers = new[] { new CallDevice("fake-speaker", "自检假扬声器") };

    private readonly object _gate = new();
    private readonly List<FakeCameraSource> _cameras = new();
    private readonly List<FakeMicrophoneSource> _microphones = new();
    private readonly List<FakeSpeakerSink> _speakers = new();

    /// <summary>最近创建的假摄像头（自检检查它推了多少帧用）。</summary>
    public FakeCameraSource? LastCamera { get; private set; }

    /// <summary>最近创建的假麦克风。</summary>
    public FakeMicrophoneSource? LastMicrophone { get; private set; }

    /// <summary>最近创建的假扬声器（自检检查它拉了多少帧用）。</summary>
    public FakeSpeakerSink? LastSpeaker { get; private set; }

    public Task<IReadOnlyList<CallDevice>> ListCamerasAsync(CancellationToken ct = default)
        => Task.FromResult(Cameras);

    public IReadOnlyList<CallDevice> ListMicrophones() => Microphones;

    public IReadOnlyList<CallDevice> ListSpeakers() => Speakers;

    public ICameraSource CreateCamera()
    {
        var camera = new FakeCameraSource();
        lock (_gate)
        {
            _cameras.Add(camera);
            LastCamera = camera;
        }
        return camera;
    }

    public IMicrophoneSource CreateMicrophone()
    {
        var microphone = new FakeMicrophoneSource();
        lock (_gate)
        {
            _microphones.Add(microphone);
            LastMicrophone = microphone;
        }
        return microphone;
    }

    public IAudioSink CreateSpeaker()
    {
        var speaker = new FakeSpeakerSink();
        lock (_gate)
        {
            _speakers.Add(speaker);
            LastSpeaker = speaker;
        }
        return speaker;
    }

    /// <summary>兜底释放所有创建过的假设备（引擎正常会自己释放；这里重复释放是幂等的）。</summary>
    public async Task DisposeCreatedAsync()
    {
        FakeCameraSource[] cameras;
        FakeMicrophoneSource[] microphones;
        FakeSpeakerSink[] speakers;
        lock (_gate)
        {
            cameras = _cameras.ToArray();
            microphones = _microphones.ToArray();
            speakers = _speakers.ToArray();
            _cameras.Clear();
            _microphones.Clear();
            _speakers.Clear();
        }

        foreach (var camera in cameras)
        {
            try { await camera.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        foreach (var microphone in microphones)
        {
            try { microphone.Dispose(); } catch { }
        }
        foreach (var speaker in speakers)
        {
            try { speaker.Dispose(); } catch { }
        }
    }
}
