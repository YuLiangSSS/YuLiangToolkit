using System.Threading.Channels;

namespace YuLiangToolkit.Tools.ChatRoom.Call;

/// <summary>
/// 通话引擎：把「采集 → 编码 → 发送」「接收 → 解码 → 播放 / 显示」串起来。
///
/// <para><b>职责边界</b>：引擎只管「把设备和网络接起来」，不认识 WPF、不认识界面：
/// 摄像头裸帧 / 远端 JPEG 都以 <see cref="CallRawFrame"/> 交给界面，由界面自己转 Bitmap。
/// 设备怎么开、网络怎么发分别由 <see cref="ICallMediaFactory"/> 和 <see cref="ICallTransport"/>
/// 的实现负责（真实设备层 / 网络层；自检可以塞假设备、假传输，整条链路无头跑）。</para>
///
/// <para><b>线程模型（重要）</b>：</para>
/// <list type="bullet">
///   <item><b>麦克风线程</b>（<see cref="IMicrophoneSource.SamplesAvailable"/>）：重采样到 16 kHz、
///         攒满 640 样本一包、算 RMS、μ-law 编码后交给 transport。只碰 <c>_micGate</c>，
///         不碰界面、不阻塞。</item>
///   <item><b>扬声器线程</b>（<see cref="IAudioSink.Start"/> 的 pull 回调）：调 <see cref="MixPull"/>，
///         里面只是 <see cref="AudioMixer.Mix"/>（混音器自己加锁），返回新数组。</item>
///   <item><b>摄像头采集回调</b>（WinRT 线程池）：只做 fps 节流 + 写长度 2 的有界 Channel，
///         **任何一步慢都直接丢帧，绝不阻塞采集回调**；降采样 / JPEG 编码在视频 worker 线程做。</item>
///   <item><b>视频 worker</b>：Channel 消费者，负责降采样、发本机预览事件、JPEG 编码、发送。</item>
///   <item><b>网络读线程</b>（<see cref="HandlePacket"/>，多路连接时可能并发）：音频解码进
///         <see cref="AudioMixer"/>、视频只统计 + 转发事件；方法里没有会长时间持锁的分支。</item>
///   <item><b>界面 / 会话线程</b>：<see cref="SetMicOn"/> / <see cref="SetCameraOn"/> / <see cref="RemoveParticipant"/>；
///         摄像头的开 / 关设备动作是异步的（扔线程池串行做），不会卡住 UI。</item>
/// </list>
///
/// <para><b>生命周期</b>：构造 → <see cref="StartAsync"/>（先扬声器，再麦克风，再摄像头；
/// 摄像头失败**不**整体失败，降级成语音通话）→ 通话中 → <see cref="StopAsync"/>（幂等）/
/// <see cref="DisposeAsync"/>（= StopAsync + 释放 transport）。<see cref="StartAsync"/> 失败时
/// 已启动的设备全部停掉，返回给用户看的中文原因。</para>
///
/// <para><b>失败语义</b>：<see cref="Failed"/> 只在「通话已经起来、但有不可恢复错误」时触发
/// （目前是扬声器设备中途失效；摄像头 / 麦克风出问题优先降级而不是挂断）。
/// 事件订阅方的异常都被吞掉，不允许把采集 / 网络线程带崩。</para>
/// </summary>
public sealed class CallEngine : IAsyncDisposable
{
    /// <summary>视频帧队列容量：只留 2 帧，编码跟不上就丢新旧帧（实时通话宁丢不卡）。</summary>
    private const int VideoChannelCapacity = 2;

    /// <summary>一包音频的样本数（640 = 40 ms @ 16 kHz）。</summary>
    private const int MicSamplesPerFrame = CallProtocolRules.AudioSamplesPerFrame;

    /// <summary>单包音频最多解码多少个样本（防御异常大的包把一块线程栈 / 临时缓冲撑爆）。</summary>
    private const int MaxAudioSamplesPerPacket = CallProtocolRules.AudioSampleRate * 2;

    private readonly ICallTransport _transport;
    private readonly ICallMediaFactory _mediaFactory;
    private readonly CallSettings _settings;
    private readonly string _selfMemberId;

    /// <summary>设备字段 / 视频 worker 句柄 / 本机摄像头状态用的锁（不持锁做设备操作）。</summary>
    private readonly object _gate = new();

    /// <summary>麦克风重采样状态用的锁（采集线程与 <see cref="SetMicOn"/> 之间）。</summary>
    private readonly object _micGate = new();

    // ---------------- 设备与运行时状态 ----------------
    private IAudioSink? _speaker;
    private IMicrophoneSource? _mic;
    private ICameraSource? _camera;
    private AudioMixer? _mixer;

    private PcmResampler? _micResampler;
    private float[] _micResampleBuffer = Array.Empty<float>();
    private readonly float[] _micFrameBuffer = new float[MicSamplesPerFrame];
    private int _micFrameCount;

    private Channel<CameraFrameData>? _videoChannel;
    private CancellationTokenSource? _videoCts;
    private Task? _videoTask;
    private long _lastAcceptedFrameTick;

    private CancellationTokenSource? _statsCts;
    private Task? _statsTask;

    /// <summary>摄像头开 / 关动作的串行闸门（设备动作慢，不能同时开两个）。</summary>
    private readonly SemaphoreSlim _cameraSwitchGate = new(1, 1);

    /// <summary>用户希望的摄像头状态（0 / 1）；实际开关由异步切换任务对齐它。</summary>
    private int _cameraWanted;

    private int _running;
    private int _micOn;
    private int _cameraOn;
    private int _cameraAvailable;
    private int _microphoneAvailable;
    private int _speakerFailureReported;
    private int _disposed;

    // ---------------- 统计计数（全部 Interlocked 更新） ----------------
    private long _sentBytes;
    private long _receivedBytes;
    private int _sentAudioPackets;
    private int _sentVideoPackets;
    private int _receivedAudioPackets;
    private int _receivedVideoPackets;
    private long _sentVideoFrames;

    /// <summary>
    /// 建一个通话引擎。构造很便宜（不开任何设备），真正打开设备在 <see cref="StartAsync"/>。
    /// </summary>
    public CallEngine(ICallTransport transport, ICallMediaFactory mediaFactory, CallSettings settings, string selfMemberId)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _mediaFactory = mediaFactory ?? throw new ArgumentNullException(nameof(mediaFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _selfMemberId = selfMemberId ?? "";
    }

    /// <summary>引擎跑着没有（<see cref="StartAsync"/> 成功后为 true，<see cref="StopAsync"/> 后为 false）。</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>麦克风开着没有（false = 不采集也不发送音频；隐私开关）。</summary>
    public bool MicOn => Volatile.Read(ref _micOn) != 0;

    /// <summary>摄像头开着没有（false = 不采集，摄像头指示灯灭）。</summary>
    public bool CameraOn => Volatile.Read(ref _cameraOn) != 0;

    /// <summary>这台机器有没有可用摄像头（<see cref="StartAsync"/> 后才知道；开了视频但打不开时为 false）。</summary>
    public bool CameraAvailable => Volatile.Read(ref _cameraAvailable) != 0;

    /// <summary>这台机器有没有可用麦克风。</summary>
    public bool MicrophoneAvailable => Volatile.Read(ref _microphoneAvailable) != 0;

    /// <summary>本机摄像头预览帧（BGRA，<see cref="CallRawFrame.IsJpeg"/> = false）。在视频 worker 线程触发。</summary>
    public event Action<CallRawFrame>? LocalVideoFrame;

    /// <summary>收到的远端视频帧（JPEG，<see cref="CallRawFrame.IsJpeg"/> = true，MemberId = 发送方）。在网络读线程触发。</summary>
    public event Action<CallRawFrame>? RemoteVideoFrame;

    /// <summary>约每秒一次的通话统计。在统计线程触发。</summary>
    public event Action<CallStats>? StatsChanged;

    /// <summary>不可恢复的错误（例如扬声器设备中途失效）；界面提示并结束通话。</summary>
    public event Action<string>? Failed;

    /// <summary>麦克风音量（0~1，给界面画电平条；静音 / 停止时为 0）。在麦克风线程触发。</summary>
    public event Action<float>? MicrophoneLevel;

    // ================================================================ 启动 / 停止

    /// <summary>
    /// 打开设备并开始收发。返回失败时引擎已经回到「没在跑」的状态（不会留下半开的设备）。
    /// </summary>
    public async Task<(bool Ok, string Error)> StartAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return (false, "通话引擎已经释放，不能再启动");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return (false, "通话已经在进行中");

        try
        {
            ResetCounters();
            Volatile.Write(ref _speakerFailureReported, 0);
            Volatile.Write(ref _lastAcceptedFrameTick, 0);

            // ---------------- 1. 扬声器（最关键：没有它连别人的声音都听不到，失败直接算启动失败） ----------------
            IAudioSink speaker;
            try
            {
                speaker = _mediaFactory.CreateSpeaker();
            }
            catch (Exception ex)
            {
                return await FailStartAsync($"打开扬声器失败：{ex.Message}").ConfigureAwait(false);
            }

            try
            {
                var (started, error) = speaker.Start(_settings.SpeakerDeviceId, MixPull);
                if (!started)
                {
                    try { speaker.Dispose(); } catch { }
                    return await FailStartAsync($"打开扬声器失败：{DescribeError(error)}").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                try { speaker.Dispose(); } catch { }
                return await FailStartAsync($"打开扬声器失败：{ex.Message}").ConfigureAwait(false);
            }

            lock (_gate) _speaker = speaker;
            // 混音率由声卡决定（48k / 44.1k）；mixer 建好后 MixPull 才有内容，之前的首帧返回静音
            var mixSampleRate = speaker.SampleRate > 0 ? speaker.SampleRate : 48000;
            Volatile.Write(ref _mixer, new AudioMixer(mixSampleRate));

            // ---------------- 2. 麦克风（EnableAudio 时才开；失败直接算启动失败，因为这是用户明确要的） ----------------
            if (_settings.EnableAudio)
            {
                var micError = TryStartMicrophone();
                if (micError.Length > 0)
                    return await FailStartAsync(micError).ConfigureAwait(false);
            }
            else
            {
                Volatile.Write(ref _micOn, 0);
                Volatile.Write(ref _microphoneAvailable, ProbeMicrophoneAvailable() ? 1 : 0);
            }

            // ---------------- 3. 摄像头（EnableVideo 时才开；失败**不**整体失败，降级成语音） ----------------
            if (_settings.EnableVideo)
            {
                Volatile.Write(ref _cameraWanted, 1);
                await OpenCameraCoreAsync().ConfigureAwait(false);
            }
            else
            {
                Volatile.Write(ref _cameraOn, 0);
                var cameraAvailable = await ProbeCameraAvailableAsync().ConfigureAwait(false);
                Volatile.Write(ref _cameraAvailable, cameraAvailable ? 1 : 0);
            }

            // ---------------- 4. 统计（约 1 秒一次快照） ----------------
            var statsCts = new CancellationTokenSource();
            _statsCts = statsCts;
            _statsTask = Task.Run(() => StatsLoopAsync(statsCts.Token));

            return (true, "");
        }
        catch (Exception ex)
        {
            return await FailStartAsync($"通话启动失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 停止收发并释放所有设备（幂等；重复调用 / 没启动过都安全）。
    /// </summary>
    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _running, 0);
        Volatile.Write(ref _cameraWanted, 0);

        // 1. 先停统计：避免停止过程中还在发快照
        var statsCts = _statsCts;
        var statsTask = _statsTask;
        _statsCts = null;
        _statsTask = null;
        try { statsCts?.Cancel(); } catch { }
        if (statsTask is not null)
        {
            try { await statsTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        statsCts?.Dispose();

        // 2. 摄像头 + 视频 worker（先让采集 / 编码停下来）
        await StopCameraCoreAsync().ConfigureAwait(false);
        Volatile.Write(ref _cameraOn, 0);
        Volatile.Write(ref _cameraAvailable, 0);

        // 3. 麦克风
        StopMicrophoneCore();
        Volatile.Write(ref _microphoneAvailable, 0);
        Volatile.Write(ref _micOn, 0);
        Raise(MicrophoneLevel, 0f);

        // 4. 扬声器最后停（前面的都停了，它就没有数据可拉，避免播放线程拿到半截状态）
        IAudioSink? speaker;
        lock (_gate)
        {
            speaker = _speaker;
            _speaker = null;
        }
        Volatile.Write(ref _mixer, null);
        try { speaker?.Dispose(); } catch { }

        // 5. 清掉混音残留，防止下一次 Start 用旧数据
        lock (_micGate)
        {
            _micFrameCount = 0;
            _micResampleBuffer = Array.Empty<float>();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await StopAsync().ConfigureAwait(false); } catch { }
        try { _transport.Dispose(); } catch { }
        // _cameraSwitchGate 故意不 Dispose：可能还有排队中的后台切换任务，SemaphoreSlim
        // 在没访问 WaitHandle 时不需要显式释放，交给 GC 比冒 ObjectDisposedException 更安全。
        GC.SuppressFinalize(this);
    }

    /// <summary>启动失败的统一收尾：停掉已经起来的设备，把原因原样返回给界面。</summary>
    private async Task<(bool Ok, string Error)> FailStartAsync(string error)
    {
        try { await StopAsync().ConfigureAwait(false); } catch { }
        return (false, error);
    }

    /// <summary>设备返回的错误文本可能是空的，补一句人话。</summary>
    private static string DescribeError(string error)
        => string.IsNullOrWhiteSpace(error) ? "设备没有给出原因（可能被其它程序占用）" : error;

    // ================================================================ 麦克风

    /// <summary>打开麦克风并接上采集回调；返回空字符串表示成功，否则返回给用户看的原因。</summary>
    private string TryStartMicrophone()
    {
        IMicrophoneSource mic;
        try
        {
            mic = _mediaFactory.CreateMicrophone();
        }
        catch (Exception ex)
        {
            return $"打开麦克风失败：{ex.Message}";
        }

        try
        {
            var (started, error) = mic.Start(_settings.MicrophoneDeviceId);
            if (!started)
            {
                try { mic.Dispose(); } catch { }
                return $"打开麦克风失败：{DescribeError(error)}";
            }

            var sampleRate = mic.SampleRate > 0 ? mic.SampleRate : 48000;
            lock (_micGate)
            {
                _micResampler = new PcmResampler(sampleRate, CallProtocolRules.AudioSampleRate);
                _micFrameCount = 0;
            }

            lock (_micGate) _mic = mic;
            Volatile.Write(ref _microphoneAvailable, 1);
            // 先落「开」的状态位再订阅：避免第一块回调因为 MicOn 还是 0 被丢掉
            Volatile.Write(ref _micOn, 1);
            mic.SamplesAvailable += OnMicSamples;
            return "";
        }
        catch (Exception ex)
        {
            try { mic.Dispose(); } catch { }
            return $"打开麦克风失败：{ex.Message}";
        }
    }

    /// <summary>停掉麦克风采集（先摘回调、再在锁外 Dispose，避免和采集线程互等）。</summary>
    private void StopMicrophoneCore()
    {
        IMicrophoneSource? mic;
        lock (_micGate)
        {
            mic = _mic;
            _mic = null;
            if (mic is not null) mic.SamplesAvailable -= OnMicSamples;
            _micResampler = null;
            _micFrameCount = 0;
        }

        // Dispose 可能会等采集线程退出；采集线程只可能在 OnMicSamples 里抢 _micGate，
        // 所以必须在释放 _micGate 之后再 Dispose，否则可能死锁。
        try { mic?.Dispose(); } catch { }
    }

    /// <summary>
    /// 麦克风采集回调（设备混音率单声道）：重采样到 16 kHz → 攒 640 样本一包 →
    /// 算 RMS → μ-law 编码 → 交给 transport。
    /// </summary>
    private void OnMicSamples(float[] buffer, int count)
    {
        if (buffer is null || count <= 0) return;
        if (Volatile.Read(ref _micOn) == 0) return;   // 已经静音：设备来不及停的最后一块直接丢

        var peakLevel = 0f;
        var sentAnyFrame = false;
        lock (_micGate)
        {
            var resampler = _micResampler;
            if (resampler is null) return;

            var inputCount = Math.Min(count, buffer.Length);
            var rate = resampler.InputRate;
            // 输出容量：(输入样本数 × 16000 / 设备率) 上取整，再留几个给块边界插值
            var needed = (int)((long)inputCount * CallProtocolRules.AudioSampleRate / rate) + 4;
            if (_micResampleBuffer.Length < needed) _micResampleBuffer = new float[needed + 256];

            var produced = resampler.Process(buffer.AsSpan(0, inputCount), _micResampleBuffer.AsSpan(0, needed));
            for (var i = 0; i < produced; i++)
            {
                _micFrameBuffer[_micFrameCount++] = _micResampleBuffer[i];
                if (_micFrameCount < MicSamplesPerFrame) continue;

                // 静音可能发生在这一块处理到一半的时候：剩下的样本也不发
                if (Volatile.Read(ref _micOn) == 0)
                {
                    _micFrameCount = 0;
                    break;
                }

                var level = ComputeRms(_micFrameBuffer, MicSamplesPerFrame);
                if (level > peakLevel) peakLevel = level;
                SendAudioFrame();
                _micFrameCount = 0;
                sentAnyFrame = true;
            }
        }

        // 事件在锁外触发：订阅方（界面）可能反过来调 SetMicOn，锁内触发会死锁。
        // 每处理满一包就发一次（包括 0），界面电平条才能掉下去。
        if (sentAnyFrame) Raise(MicrophoneLevel, Math.Clamp(peakLevel, 0f, 1f));
    }

    /// <summary>把 <see cref="_micFrameBuffer"/> 里的 640 个 16 kHz 样本编码成一包发出去。</summary>
    private void SendAudioFrame()
    {
        try
        {
            Span<byte> encoded = stackalloc byte[MicSamplesPerFrame];
            MuLawCodec.Encode(_micFrameBuffer.AsSpan(0, MicSamplesPerFrame), encoded);
            _transport.SendAudio(encoded);
            Interlocked.Add(ref _sentBytes, MicSamplesPerFrame);
            Interlocked.Increment(ref _sentAudioPackets);
        }
        catch
        {
            // 连接断了：这一包丢了无所谓，Closed 会让会话层收尾；下一包继续试
        }
    }

    /// <summary>算一段样本的 RMS（0~1），给界面画麦克风电平条。</summary>
    private static float ComputeRms(float[] samples, int count)
    {
        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            var sample = samples[i];
            sum += sample * sample;
        }
        return count <= 0 ? 0f : (float)Math.Sqrt(sum / count);
    }

    // ================================================================ 摄像头 / 视频

    /// <summary>启动时打开摄像头（失败只降级，不影响语音通话）。</summary>
    private async Task OpenCameraCoreAsync()
    {
        if (Volatile.Read(ref _camera) is not null) return;

        ICameraSource? camera = null;
        try
        {
            camera = _mediaFactory.CreateCamera();
            var (started, _) = await camera.StartAsync(_settings.CameraDeviceId).ConfigureAwait(false);
            if (!started)
            {
                Volatile.Write(ref _cameraAvailable, 0);
                Volatile.Write(ref _cameraOn, 0);
                Volatile.Write(ref _cameraWanted, 0);
                await DisposeCameraAsync(camera).ConfigureAwait(false);
                ReportControlState();
                return;
            }

            // 打开过程中用户又把摄像头关了：立刻放弃（灯已经亮过一下，但不再采集）
            if (Volatile.Read(ref _cameraWanted) == 0 || !IsRunning)
            {
                await DisposeCameraAsync(camera).ConfigureAwait(false);
                return;
            }

            camera.FrameArrived += OnCameraFrame;
            lock (_gate) _camera = camera;
            Volatile.Write(ref _lastAcceptedFrameTick, 0);
            Volatile.Write(ref _cameraAvailable, 1);
            Volatile.Write(ref _cameraOn, 1);
            StartVideoWorker();
        }
        catch
        {
            Volatile.Write(ref _cameraAvailable, 0);
            Volatile.Write(ref _cameraOn, 0);
            Volatile.Write(ref _cameraWanted, 0);
            await DisposeCameraAsync(camera).ConfigureAwait(false);
            ReportControlState();
        }
    }

    /// <summary>串行地关摄像头（等正在进行的打开动作结束后再关；等太久就放弃，别卡死挂断）。</summary>
    private async Task StopCameraCoreAsync()
    {
        var acquired = await _cameraSwitchGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!acquired) return;   // 切换任务卡在设备驱动里：它返回后自己会看到 running=0 并释放

        try
        {
            await CloseCameraCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _cameraSwitchGate.Release();
        }
    }

    /// <summary>真正关摄像头：摘回调 → 停视频 worker → 释放设备。调用方要持有 <see cref="_cameraSwitchGate"/>。</summary>
    private async Task CloseCameraCoreAsync()
    {
        ICameraSource? camera;
        lock (_gate)
        {
            camera = _camera;
            _camera = null;
        }

        if (camera is not null) camera.FrameArrived -= OnCameraFrame;

        await StopVideoWorkerAsync().ConfigureAwait(false);

        if (camera is not null)
        {
            // WinRT 的 MediaCapture 要异步关；关掉后摄像头灯才真正灭掉。
            // 加超时：设备驱动偶发卡死时不能把「挂断」也一起卡住。
            await DisposeCameraAsync(camera).ConfigureAwait(false);
        }
    }

    /// <summary>释放一个摄像头（最多等 3 秒；失败 / 超时都吞掉，挂断优先）。</summary>
    private static async Task DisposeCameraAsync(ICameraSource? camera)
    {
        if (camera is null) return;
        try
        {
            await camera.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            // 设备已经拔了 / 驱动报错：释放失败也没别的办法
        }
    }

    /// <summary>用户点开 / 关摄像头后的异步切换（可能被连续点击，循环对齐到 <see cref="_cameraWanted"/>）。</summary>
    private async Task SwitchCameraAsync()
    {
        try
        {
            await _cameraSwitchGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            while (true)
            {
                var wanted = Volatile.Read(ref _cameraWanted) != 0;
                var hasCamera = Volatile.Read(ref _camera) is not null;
                if (wanted == hasCamera) return;

                if (wanted) await OpenCameraCoreAsync().ConfigureAwait(false);
                else await CloseCameraCoreAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // 切换失败不能把后台任务变成未处理异常；状态位已经在 Open / Close 里改好
        }
        finally
        {
            _cameraSwitchGate.Release();
        }
    }

    /// <summary>摄像头采集回调（WinRT 线程）：只做 fps 节流 + 入队，绝不做重活。</summary>
    private void OnCameraFrame(CameraFrameData frame)
    {
        try
        {
            if (frame is null) return;
            if (Volatile.Read(ref _cameraOn) == 0) return;

            // 按画质档位的 fps 节流：采集回调通常是固定 30 帧，多余的直接丢，不进队列
            var fps = _settings.Video.Fps;
            if (fps <= 0) fps = 10;
            var intervalMs = Math.Max(1, 1000 / fps);
            var now = Environment.TickCount64;
            var last = Volatile.Read(ref _lastAcceptedFrameTick);
            if (now - last < intervalMs) return;
            Volatile.Write(ref _lastAcceptedFrameTick, now);

            Channel<CameraFrameData>? channel;
            lock (_gate) channel = _videoChannel;
            channel?.Writer.TryWrite(frame);   // 有界 + DropOldest：写不进去也不会阻塞采集线程
        }
        catch
        {
            // WinRT 回调里抛异常可能直接打断采集流：这里必须全吞
        }
    }

    /// <summary>为一个新打开的摄像头建队列 + 启动视频 worker。</summary>
    private void StartVideoWorker()
    {
        var channel = Channel.CreateBounded<CameraFrameData>(new BoundedChannelOptions(VideoChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,   // 编码跟不上就丢最旧，保实时
            SingleReader = true,
            SingleWriter = false,
        });

        var cts = new CancellationTokenSource();
        var reader = channel.Reader;
        var worker = Task.Run(() => VideoWorkerAsync(reader, cts.Token));

        lock (_gate)
        {
            _videoChannel = channel;
            _videoCts = cts;
            _videoTask = worker;
        }
    }

    /// <summary>停掉视频 worker 并等它退出（最多 2 秒，防止设备 / 编码卡死把挂断也卡死）。</summary>
    private async Task StopVideoWorkerAsync()
    {
        Channel<CameraFrameData>? channel;
        CancellationTokenSource? cts;
        Task? worker;
        lock (_gate)
        {
            channel = _videoChannel;
            cts = _videoCts;
            worker = _videoTask;
            _videoChannel = null;
            _videoCts = null;
            _videoTask = null;
        }

        try { channel?.Writer.TryComplete(); } catch { }
        try { cts?.Cancel(); } catch { }
        if (worker is not null)
        {
            try { await worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        cts?.Dispose();
    }

    /// <summary>视频 worker：降采样 → 本机预览事件 → JPEG 编码 → 发送。慢就丢帧，不阻塞采集。</summary>
    private async Task VideoWorkerAsync(ChannelReader<CameraFrameData> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (Volatile.Read(ref _cameraOn) == 0) continue;

                    var targetWidth = _settings.Video.Width;
                    if (targetWidth <= 0) targetWidth = 320;

                    var bgra = BgraDownscaler.Downscale(frame.Bgra, frame.Width, frame.Height, targetWidth,
                        out var width, out var height);

                    // 本机预览：先用原始（未压缩）BGRA 交给界面，跟是否上行无关
                    Raise(LocalVideoFrame, new CallRawFrame(_selfMemberId, bgra, false, width, height, frame.TimestampMs));

                    // 关了摄像头 / 连接已断：这帧省掉编码 CPU
                    if (Volatile.Read(ref _cameraOn) == 0 || !_transport.IsOpen) continue;

                    var jpeg = JpegFrameCodec.EncodeBgra(bgra, width, height, _settings.Video.JpegQuality);
                    _transport.SendVideo(jpeg);
                    Interlocked.Add(ref _sentBytes, jpeg.Length);
                    Interlocked.Increment(ref _sentVideoPackets);
                    Interlocked.Increment(ref _sentVideoFrames);
                }
                catch
                {
                    // 单帧降采样 / 编码 / 发送失败：丢掉这一帧，不影响后续
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch { /* 队列被关 / 其它异常：worker 安静退出即可 */ }
    }

    // ================================================================ 混音（扬声器拉模型）

    /// <summary>
    /// 扬声器线程的 pull 回调：返回 <c>frames × 2</c> 的 interleaved 立体声（左 = 右）。
    /// mixer 还没建好（启动首帧）时返回静音，避免空引用把播放线程带崩。
    /// </summary>
    private float[] MixPull(int frames)
    {
        var safeFrames = frames > 0 ? frames : 0;
        var output = new float[safeFrames * 2];
        var mixer = Volatile.Read(ref _mixer);
        if (mixer is null || safeFrames == 0) return output;

        try
        {
            mixer.Mix(safeFrames, output);
        }
        catch
        {
            // 混音异常（没见过，但播放线程不能崩）：这一块给静音
            Array.Clear(output);
        }
        return output;
    }

    // ================================================================ 收包 / 参会者

    /// <summary>
    /// 收到一个远端媒体包（会话层在网络线程调用，必须快速返回）。
    /// <para>音频：μ-law 解码成 float → 交给混音器；视频：只统计，然后原样把 JPEG 交给界面
    /// （引擎不解码，界面层用 <see cref="JpegFrameCodec.DecodeToBitmap"/>）。</para>
    /// </summary>
    public void HandlePacket(string fromMemberId, CallMediaKind kind, byte[] payload, long timestampMs)
    {
        if (payload is null || payload.Length == 0) return;
        var memberId = fromMemberId ?? "";

        try
        {
            if (kind == CallMediaKind.Audio)
            {
                Interlocked.Add(ref _receivedBytes, payload.Length);
                Interlocked.Increment(ref _receivedAudioPackets);

                var mixer = Volatile.Read(ref _mixer);
                if (mixer is null) return;

                // 每个线程一块复用的解码缓冲（ThreadStatic）：网络读线程固定，几乎不分配；
                // 异常大的包只解前 2 秒，避免临时缓冲被撑爆。
                var sampleCount = Math.Min(payload.Length, MaxAudioSamplesPerPacket);
                var samples = RentDecodeScratch(sampleCount);
                MuLawCodec.Decode(payload.AsSpan(0, sampleCount), samples.AsSpan(0, sampleCount));
                mixer.AddFrame(memberId, samples, sampleCount, CallProtocolRules.AudioSampleRate);
            }
            else
            {
                Interlocked.Add(ref _receivedBytes, payload.Length);
                Interlocked.Increment(ref _receivedVideoPackets);

                // 引擎不知道对端的画质档位，也不解码 JPEG，宽高填 0；界面用解码后的 PixelWidth/PixelHeight
                Raise(RemoteVideoFrame, new CallRawFrame(memberId, payload, true, 0, 0, timestampMs));
            }
        }
        catch
        {
            // 网络读线程绝不能因为一个坏包崩溃
        }
    }

    /// <summary>某个人离开通话了：清掉他的混音缓冲（视频帧是无状态的，不需要清）。</summary>
    public void RemoveParticipant(string memberId)
    {
        if (string.IsNullOrEmpty(memberId)) return;
        try { Volatile.Read(ref _mixer)?.RemoveMember(memberId); } catch { }
    }

    // ================================================================ 麦克风 / 摄像头开关

    /// <summary>
    /// 开关麦克风：关的时候**真的停掉采集**（隐私），开会重新打开；状态变化会通过 transport 上报。
    /// </summary>
    public void SetMicOn(bool value)
    {
        if (value == MicOn) return;

        if (!IsRunning)
        {
            Volatile.Write(ref _micOn, value ? 1 : 0);
            return;
        }

        if (!value)
        {
            // 先落状态位：采集线程看到 0 就不再发剩余的包
            Volatile.Write(ref _micOn, 0);
            StopMicrophoneCore();
            Raise(MicrophoneLevel, 0f);
        }
        else
        {
            var error = TryStartMicrophone();
            if (error.Length > 0)
            {
                // 打不开（被占用 / 拔了）：保持静音；但**不发 Failed** —— 还能听别人说话，
                // 直接挂断整个通话太重，界面从 MicOn 属性就能看到结果。
                Volatile.Write(ref _micOn, 0);
                ReportControlState();
                return;
            }
        }

        ReportControlState();
    }

    /// <summary>
    /// 开关摄像头：关 = 异步释放设备（摄像头灯灭）；开会异步重新打开。状态变化会通过 transport 上报。
    /// <para>摄像头的开关设备是慢操作（WinRT 几百毫秒），这里扔后台串行做，不阻塞 UI 线程。</para>
    /// </summary>
    public void SetCameraOn(bool value)
    {
        if (value == CameraOn) return;

        if (!IsRunning)
        {
            Volatile.Write(ref _cameraOn, value && CameraAvailable ? 1 : 0);
            return;
        }

        if (value && !CameraAvailable)
        {
            // 没有可用摄像头：保持关
            Volatile.Write(ref _cameraOn, 0);
            ReportControlState();
            return;
        }

        if (value)
        {
            Volatile.Write(ref _cameraOn, 1);
            Volatile.Write(ref _cameraWanted, 1);
        }
        else
        {
            Volatile.Write(ref _cameraOn, 0);
            Volatile.Write(ref _cameraWanted, 0);
        }

        _ = Task.Run(SwitchCameraAsync);
        ReportControlState();
    }

    /// <summary>把当前麦克风 / 摄像头状态上报给网络层（失败无所谓，连接断开会走 Closed）。</summary>
    private void ReportControlState()
    {
        try { _transport.SendControl(MicOn, CameraOn); } catch { }
    }

    // ================================================================ 统计

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        var lastSentBytes = Interlocked.Read(ref _sentBytes);
        var lastReceivedBytes = Interlocked.Read(ref _receivedBytes);
        var lastVideoFrames = Interlocked.Read(ref _sentVideoFrames);
        var lastTick = Environment.TickCount64;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = Environment.TickCount64;
                var elapsedMs = Math.Max(1, now - lastTick);
                var sentBytes = Interlocked.Read(ref _sentBytes);
                var receivedBytes = Interlocked.Read(ref _receivedBytes);
                var videoFrames = Interlocked.Read(ref _sentVideoFrames);

                var stats = new CallStats
                {
                    SentBytes = sentBytes,
                    ReceivedBytes = receivedBytes,
                    SentAudioPackets = Volatile.Read(ref _sentAudioPackets),
                    SentVideoPackets = Volatile.Read(ref _sentVideoPackets),
                    ReceivedAudioPackets = Volatile.Read(ref _receivedAudioPackets),
                    ReceivedVideoPackets = Volatile.Read(ref _receivedVideoPackets),
                    // bytes/毫秒 × 8 = kbit/s（1 kbps = 1000 bit/s = 1 bit/ms）
                    SendKbps = (sentBytes - lastSentBytes) * 8.0 / elapsedMs,
                    ReceiveKbps = (receivedBytes - lastReceivedBytes) * 8.0 / elapsedMs,
                    SendVideoFps = (videoFrames - lastVideoFrames) * 1000.0 / elapsedMs,
                    DroppedVideoFrames = SafeDroppedVideoFrames(),
                };

                lastSentBytes = sentBytes;
                lastReceivedBytes = receivedBytes;
                lastVideoFrames = videoFrames;
                lastTick = now;

                CheckSpeakerHealth();
                Raise(StatsChanged, stats);
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch { /* 统计线程不能把通话带崩 */ }
    }

    /// <summary>transport 的丢帧计数可能抛（断开等场景），拿不到就当 0。</summary>
    private int SafeDroppedVideoFrames()
    {
        try { return _transport.DroppedVideoFrames; } catch { return 0; }
    }

    /// <summary>
    /// 检查扬声器是否还活着（每约 1 秒一次）。扬声器失效 = 整通电话听不到声音，
    /// 属于 <see cref="Failed"/> 定义的「不可恢复错误」；只上报一次，避免每秒刷屏。
    /// </summary>
    private void CheckSpeakerHealth()
    {
        IAudioSink? speaker;
        lock (_gate) speaker = _speaker;
        if (speaker is null) return;

        string? error;
        try { error = speaker.LastError; } catch { return; }
        if (string.IsNullOrEmpty(error)) return;
        if (Interlocked.Exchange(ref _speakerFailureReported, 1) != 0) return;

        Raise(Failed, $"扬声器出错了：{error}");
    }

    /// <summary>把统计计数清零（每次 StartAsync 从干净状态开始）。</summary>
    private void ResetCounters()
    {
        Interlocked.Exchange(ref _sentBytes, 0);
        Interlocked.Exchange(ref _receivedBytes, 0);
        Interlocked.Exchange(ref _sentAudioPackets, 0);
        Interlocked.Exchange(ref _sentVideoPackets, 0);
        Interlocked.Exchange(ref _receivedAudioPackets, 0);
        Interlocked.Exchange(ref _receivedVideoPackets, 0);
        Interlocked.Exchange(ref _sentVideoFrames, 0);
    }

    // ================================================================ 设备探测 / 小工具

    /// <summary>没开音频时探测一下麦克风是否存在（只枚举，不打开设备）。</summary>
    private bool ProbeMicrophoneAvailable()
    {
        try { return _mediaFactory.ListMicrophones().Count > 0; } catch { return false; }
    }

    /// <summary>没开视频时探测一下摄像头是否存在（只枚举，不打开设备、不亮灯）。</summary>
    private async Task<bool> ProbeCameraAvailableAsync()
    {
        try
        {
            var cameras = await _mediaFactory.ListCamerasAsync().ConfigureAwait(false);
            return cameras.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>每线程复用的音频解码缓冲（网络读线程固定，所以几乎只分配一次）。</summary>
    [ThreadStatic]
    private static float[]? _decodeScratch;

    private static float[] RentDecodeScratch(int length)
    {
        var buffer = _decodeScratch;
        if (buffer is null || buffer.Length < length)
        {
            buffer = new float[Math.Max(length, MicSamplesPerFrame)];
            _decodeScratch = buffer;
        }
        return buffer;
    }

    /// <summary>安全触发事件：订阅方抛异常不能把采集 / 网络 / 播放线程带崩。</summary>
    private static void Raise<T>(Action<T>? handler, T value)
    {
        if (handler is null) return;
        try { handler(value); } catch { }
    }
}
