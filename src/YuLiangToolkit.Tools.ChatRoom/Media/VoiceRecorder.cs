using System.Diagnostics;
using YuLiangToolkit.Tools.ChatRoom.Call;

namespace YuLiangToolkit.Tools.ChatRoom.Media;

// ============================================================================
// 「按住录音 → 松开自动发送语音消息」的录音层（联机聊天室用）。
//
// 职责：打开麦克风 → 边录边写 PCM16 单声道 WAV（WavWriter）→ 停止时转成 MP3
// （Mp3Encoder）→ 返回临时 MP3 路径 + 秒数。网络发送 / 界面交互都不在这一层。
//
// 为什么这么做：
//   · 复用通话已有的 ICallMediaFactory / IMicrophoneSource（WASAPI 共享模式采集），
//     不再写一套录音设备代码，也天然支持「记住上次用的麦克风」；
//   · 录音是「按住就录、松手就发」，所以边录边写 WAV（顺序写几乎零 CPU），
//     松手后只剩一次系统 MP3 转码，按住时间 ≈ 发送延迟；
//   · 临时文件统一放 %TEMP%\ylt-chat-voice\，成功只把 MP3 交出去（调用方发完自己删），
//     WAV 在转码成功后立刻删；失败 / 取消必须清理残留，不能把临时文件越攒越多。
//
// 线程模型：
//   · 音频回调（OnSamplesAvailable）在 WASAPI 采集线程上高频触发，只做「转 PCM16 + 写文件」，
//     不碰界面、不抛异常（写失败记进 LastError 并停下）；
//   · StartAsync / StopAsync 用 SemaphoreSlim 串行，重复调用安全；Cancel 是同步的
//     「急停 + 清理」，可以从 UI 线程（关闭 / 切走）直接调用，不抢那把异步锁；
//   · 停止顺序固定为「先摘事件 → Dispose 采集器（内部会 Join 采集线程）→ 再收 WAV」，
//     保证不会出现「采集线程正在写、另一线程却在关文件」。
//
// 已知边界：
//   · 120 秒上限（MaxDurationSeconds）由 ViewModel 负责到点调 StopAsync：这一层不裁剪，
//     Duration 如实反映真实录制时长；即使调用方忘了停也只是持续写磁盘（约 96 KB/s），
//     不会崩；
//   · 没有回声消除（AEC），录的时候别外放，否则会把对方的声音录进去；
//   · 没有媒体功能包的 Windows N / KN 版转不了 MP3（Mp3Encoder 返回中文原因）；
//   · 录音中途设备被拔掉：采集线程会把原因写进 LastError，StopAsync 仍然会把已录到的
//     声音尽量落盘并转码（拿不到声音时返回中文失败原因）。
// ============================================================================

/// <summary>
/// 一次「按住录音」的结果（<see cref="VoiceRecorder.StopAsync"/> 的返回值）。
/// </summary>
/// <param name="Ok">是否成功产出 MP3。</param>
/// <param name="Mp3Path">成功时的临时 MP3 完整路径（调用方发完自行删除）；失败为 null。</param>
/// <param name="DurationSeconds">录音时长（秒）：<c>Math.Max(1, (int)Math.Round(Duration.TotalSeconds, AwayFromZero))</c>，失败时可能为 0。</param>
/// <param name="Error">失败原因（中文）；成功时是空字符串。</param>
public sealed record VoiceRecordingResult(bool Ok, string? Mp3Path, int DurationSeconds, string Error);

/// <summary>
/// 语音消息录音器：按住开始（<see cref="StartAsync"/>）、松开停止（<see cref="StopAsync"/>）、
/// 转码出临时 MP3；<see cref="Cancel"/> 用于取消发送 / 切走时丢弃。
///
/// <para><b>为什么这么做</b>：语音消息对延迟最敏感的是「松手到能发」这一段，
/// 所以录音阶段只做最便宜的 PCM16 落盘，把 CPU 都省给松手后的那一次 MP3 转码。</para>
///
/// <para><b>线程模型</b>：<see cref="StartAsync"/> / <see cref="StopAsync"/> 内部用
/// <see cref="SemaphoreSlim"/> 串行；<see cref="Cancel"/> 是同步急停，不与它们抢锁。
/// 采集回调在 WASAPI 线程上触发，只写文件、不抛异常；属性可以任意线程读。</para>
///
/// <para><b>已知边界</b>：120 秒上限由调用方（ViewModel）控制，这一层只如实上报
/// <see cref="Duration"/>；没有 AEC；Windows N / KN 版缺媒体功能包时转码失败并返回中文原因；
/// 麦克风被其它程序独占 / 被拔掉时 StartAsync 返回中文原因且不残留临时文件。</para>
/// </summary>
public sealed class VoiceRecorder : IDisposable
{
    /// <summary>单条语音消息的时长上限（秒）。到点由 ViewModel 调 <see cref="StopAsync"/>；这一层不强制裁剪。</summary>
    public const int MaxDurationSeconds = 120;

    /// <summary>临时目录：WAV 与 MP3 都放这里，成功后只留 MP3 交给调用方。</summary>
    private static readonly string TempDirectory = Path.Combine(Path.GetTempPath(), "ylt-chat-voice");

    private readonly ICallMediaFactory _mediaFactory;
    private readonly string _microphoneDeviceId;

    /// <summary>串行化 StartAsync / StopAsync（录音生命周期只有一条；Cancel 不走这把锁）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>保护 _mic / _wavPath / _mp3Path / _stopCts / _disposed 这些「交接状态」。</summary>
    private readonly object _stateLock = new();

    /// <summary>保护 _wav：采集线程要高频拿它，停止线程要「先摘事件再收尾」，用专门的短锁。</summary>
    private readonly object _callbackLock = new();

    /// <summary>当前采集器（打开麦克风后才有；Stop / Cancel 会先摘事件再 Dispose）。</summary>
    private IMicrophoneSource? _mic;

    /// <summary>当前 WAV 写入器（受 _callbackLock 保护）。</summary>
    private WavWriter? _wav;

    /// <summary>本次录音的 WAV 临时路径（成功转码后清空；失败 / 取消时删文件）。</summary>
    private string? _wavPath;

    /// <summary>本次录音的 MP3 临时路径（成功时清空并把路径交给调用方，之后不再归本对象管）。</summary>
    private string? _mp3Path;

    /// <summary>正在进行的 StopAsync 的取消源（Cancel 用它把转码打断）。</summary>
    private CancellationTokenSource? _stopCts;

    /// <summary>是否正在录音（音频回调与属性读取都看它）。</summary>
    private volatile bool _recording;

    /// <summary>设备混音采样率（Start 成功后才有意义；用于计算 Duration）。</summary>
    private volatile int _sampleRate;

    /// <summary>已录样本数（采集线程累加，别的线程读）。</summary>
    private long _samples;

    /// <summary>写 WAV 失败过（磁盘满等）：StopAsync 据此返回失败而不是假装成功。</summary>
    private volatile bool _writeFailed;

    /// <summary>最近一次错误（中文；空 = 没有）。</summary>
    private volatile string? _lastError;

    private bool _disposed;

    /// <summary>
    /// 创建录音器（不打开设备，创建很便宜）。
    /// </summary>
    /// <param name="mediaFactory">媒体工厂（真实设备用 <see cref="Call.WindowsCallMediaFactory.Shared"/>，自检可塞假的）。</param>
    /// <param name="microphoneDeviceId">麦克风设备 Id（空 = 系统默认）；就是 WASAPI 端点 Id。</param>
    public VoiceRecorder(ICallMediaFactory mediaFactory, string microphoneDeviceId)
    {
        _mediaFactory = mediaFactory ?? throw new ArgumentNullException(nameof(mediaFactory));
        _microphoneDeviceId = (microphoneDeviceId ?? string.Empty).Trim();
    }

    /// <summary>是否正在录音（<see cref="StopAsync"/> / <see cref="Cancel"/> 之后为 false）。</summary>
    public bool IsRecording => _recording;

    /// <summary>
    /// 已录时长（按设备采样率算，如实反映；停止后保留最后一次的值，下次 <see cref="StartAsync"/> 归零）。
    /// <para>与 <see cref="MaxDurationSeconds"/> 无关：真录超了也照实显示，裁剪是 ViewModel 的事。</para>
    /// </summary>
    public TimeSpan Duration
    {
        get
        {
            var rate = _sampleRate;
            var samples = Interlocked.Read(ref _samples);
            if (rate <= 0 || samples <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds((double)samples / rate);
        }
    }

    /// <summary>最近一次错误（中文；空 = 没有）。StartAsync / StopAsync 的返回值里也有同样的原因。</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// 打开麦克风并开始录音。失败返回中文原因（不抛异常），且不残留临时文件。
    /// <para>重复调用：正在录音时返回「正在录音」；上一次已停时可以直接再开一条。</para>
    /// </summary>
    /// <returns>(是不是开始成功, 失败原因)。</returns>
    public async Task<(bool Ok, string Error)> StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return Fail("录音器已经释放，不能再录音");
            if (_recording || Volatile.Read(ref _mic) is not null) return (false, "正在录音，请先停止再重新开始");

            // 上一次的残留（Stop 失败 / Cancel 没跑完 / 进程内异常）先清掉，避免临时目录越攒越多。
            CleanupTempFiles();
            SetError(null);
            _writeFailed = false;
            Interlocked.Exchange(ref _samples, 0);
            _sampleRate = 0;

            // 一次录音一对同名临时文件：WAV 是中间产物，MP3 是最终产物。
            var token = Guid.NewGuid().ToString("N");
            var wavPath = Path.Combine(TempDirectory, $"voice-{token}.wav");
            var mp3Path = Path.Combine(TempDirectory, $"voice-{token}.mp3");
            lock (_stateLock)
            {
                _wavPath = wavPath;
                _mp3Path = mp3Path;
            }

            IMicrophoneSource? mic = null;
            WavWriter? wav = null;
            var subscribed = false;
            try
            {
                Directory.CreateDirectory(TempDirectory);

                mic = _mediaFactory.CreateMicrophone();
                var (ok, error) = mic.Start(_microphoneDeviceId);
                if (!ok)
                {
                    TryDispose(mic);
                    mic = null;
                    CleanupTempFiles();
                    return Fail(string.IsNullOrWhiteSpace(error)
                        ? "打开麦克风失败（没有可用的录音设备，或设备被其它程序占用）"
                        : error);
                }

                var sampleRate = mic.SampleRate;
                if (sampleRate <= 0)
                {
                    TryDispose(mic);
                    mic = null;
                    CleanupTempFiles();
                    return Fail("麦克风没有报告采样率，无法录音（设备可能已被拔出）");
                }

                // 先建 WAV（确定采样率后才知道头怎么写），再挂事件开始收样本。
                wav = new WavWriter(wavPath, sampleRate);
                _sampleRate = sampleRate;
                lock (_callbackLock) { _wav = wav; }
                mic.SamplesAvailable += OnSamplesAvailable;
                subscribed = true;
                lock (_stateLock) { _mic = mic; }
                _recording = true;

                // 所有权已经交给字段（_wav / _mic）：这里置空，避免下面的 catch 重复释放。
                wav = null;
                mic = null;
                return (true, "");
            }
            catch (Exception ex)
            {
                if (subscribed && mic is not null)
                {
                    try { mic.SamplesAvailable -= OnSamplesAvailable; } catch { /* 摘事件失败不影响清理 */ }
                }
                TryDispose(mic);
                WavWriter? pendingWav;
                lock (_callbackLock) { pendingWav = _wav; _wav = null; }
                if (pendingWav is not null) try { pendingWav.Dispose(); } catch { }
                lock (_stateLock) { _mic = null; }
                _recording = false;
                _sampleRate = 0;
                CleanupTempFiles();
                return Fail("开始录音失败：" + ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 停止录音：收 WAV → 转 MP3 → 删 WAV，返回临时 MP3 路径与秒数。不抛异常
    /// （取消 / 设备异常都转成 <c>Ok = false</c> + 中文原因）。
    /// <para>重复调用：没有正在进行的录音时返回「当前没有正在进行的录音」。</para>
    /// </summary>
    public async Task<VoiceRecordingResult> StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return new VoiceRecordingResult(false, null, 0, "录音器已经释放");

            IMicrophoneSource? mic;
            lock (_stateLock)
            {
                mic = _mic;
                _mic = null;
            }

            // 注意：不能只看 _recording —— 写文件失败时它已被置 false，但采集器还在跑，仍需收尾。
            if (!_recording && mic is null) return new VoiceRecordingResult(false, null, 0, "当前没有正在进行的录音");

            // 1) 停采集：先摘事件（防止新回调进来），再 Dispose（内部会 Join 采集线程）。
            //    这两步都在任何锁之外做，避免和「回调里拿 _callbackLock」形成锁顺序倒挂。
            _recording = false;
            if (mic is not null)
            {
                try { mic.SamplesAvailable -= OnSamplesAvailable; } catch { /* 事件已摘过也忽略 */ }
                TryDispose(mic);
            }

            // 2) 收 WAV：此时采集线程已退出，回调不会再进来；Finish 回填长度并关文件。
            WavWriter? wav;
            lock (_callbackLock)
            {
                wav = _wav;
                _wav = null;
            }
            string? finishError = null;
            if (wav is not null)
            {
                try { wav.Finish(); }
                catch (Exception ex) { finishError = "保存 WAV 录音失败：" + ex.Message; }
                finally { try { wav.Dispose(); } catch { /* 已在 Finish 里失败过，忽略重复关闭 */ } }
            }

            // 3) 汇总时长与临时路径。
            var sampleRate = _sampleRate;
            var samples = Interlocked.Read(ref _samples);
            var seconds = SecondsOf(samples, sampleRate);

            string wavPath, mp3Path;
            lock (_stateLock)
            {
                wavPath = _wavPath ?? string.Empty;
                mp3Path = _mp3Path ?? string.Empty;
            }

            if (finishError is not null || _writeFailed)
            {
                var message = finishError ?? _lastError ?? "写入录音文件失败";
                CleanupTempFiles();
                SetError(message);
                return new VoiceRecordingResult(false, null, seconds, message);
            }

            if (samples <= 0 || wavPath.Length == 0 || mp3Path.Length == 0)
            {
                const string message = "没有录到声音（麦克风没有采集到数据，可能被其它程序占用或设备已拔出）";
                CleanupTempFiles();
                SetError(message);
                return new VoiceRecordingResult(false, null, 0, message);
            }

            // 4) 异步转码（这是松手后唯一耗时步骤）。Cancel 会取消它，下面的 catch 负责清理。
            var cts = new CancellationTokenSource();
            lock (_stateLock) { _stopCts = cts; }
            try
            {
                var (ok, error) = await Mp3Encoder
                    .TranscodeWavToMp3Async(wavPath, mp3Path, cts.Token)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    CleanupTempFiles();
                    SetError(error);
                    return new VoiceRecordingResult(false, null, seconds, error);
                }

                // 成功：WAV 是中间产物，删掉；MP3 路径「交出去」——字段清空，之后 Cancel / Dispose
                // 都不会再删它（调用方发完消息自己删）。
                TryDelete(wavPath);
                lock (_stateLock)
                {
                    _wavPath = null;
                    _mp3Path = null;
                }
                return new VoiceRecordingResult(true, mp3Path, seconds, "");
            }
            catch (OperationCanceledException)
            {
                CleanupTempFiles();
                const string message = "录音已取消";
                SetError(message);
                return new VoiceRecordingResult(false, null, seconds, message);
            }
            catch (Exception ex)
            {
                // Mp3Encoder 契约上只在取消时抛；这里兜底，保证 StopAsync 永远不把异常甩给 ViewModel。
                CleanupTempFiles();
                var message = "语音转码失败：" + ex.Message;
                SetError(message);
                return new VoiceRecordingResult(false, null, seconds, message);
            }
            finally
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_stopCts, cts)) _stopCts = null;
                }
                cts.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 取消录音：停采集 + 丢弃临时文件（WAV / MP3 都删）。可重复调用。
    /// <para>如果此刻 <see cref="StopAsync"/> 正在转码，会顺手取消它（StopAsync 自己负责清理），
    /// 所以 Cancel 既能在「正在按住」时用，也能在「松手后、发送前」用。</para>
    /// </summary>
    public void Cancel()
    {
        IMicrophoneSource? mic;
        CancellationTokenSource? stopCts;
        lock (_stateLock)
        {
            _recording = false;
            mic = _mic;
            _mic = null;
            stopCts = _stopCts;
            _stopCts = null;
        }

        // 1) 停采集（Dispose 会 Join 采集线程，可能花几十毫秒，放在锁外）。
        if (mic is not null)
        {
            try { mic.SamplesAvailable -= OnSamplesAvailable; } catch { /* 已经摘过就忽略 */ }
            TryDispose(mic);
        }

        // 2) 打断正在进行的转码（CTS 可能已被 StopAsync 的 finally 释放，吞掉异常）。
        if (stopCts is not null)
        {
            try { stopCts.Cancel(); } catch (ObjectDisposedException) { /* StopAsync 刚好收尾了 */ }
        }

        // 3) 收掉可能还开着的 WAV（正常情况下 StopAsync 会先摘走，这里只兜 Cancel 发生在录音中）。
        WavWriter? wav;
        lock (_callbackLock)
        {
            wav = _wav;
            _wav = null;
        }
        if (wav is not null)
        {
            try { wav.Finish(); } catch { /* 取消路径不关心头是否回填成功 */ }
            try { wav.Dispose(); } catch { }
        }

        // 4) 删临时文件（_mp3Path 在成功 Stop 时已清空，所以不会误删已交给调用方的 MP3）。
        CleanupTempFiles();
        Trace.WriteLine("[语音消息] 已取消录音并清理临时文件");
    }

    /// <summary>释放录音器（= <see cref="Cancel"/>；不会删已经交给调用方的 MP3）。可重复调用。</summary>
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // 先把还在进行的 StopAsync 的转码取消掉，再走通用清理。
        Cancel();

        // 故意不 Dispose _gate：可能还有 StopAsync 卡在 WaitAsync 上，释放信号量会让它抛
        // ObjectDisposedException；SemaphoreSlim 没创建 WaitHandle 时没有非托管资源，留给 GC 即可。
    }

    // ===================== 采集回调 =====================

    /// <summary>
    /// WASAPI 采集线程回调：把设备混音率单声道 float 追加进 WAV 并累加样本数。
    /// <para>必须快速返回、绝不抛异常：写失败（磁盘满 / 文件被删）只记 <see cref="LastError"/>
    /// 并停止写入，真正的清理留给 StopAsync / Cancel。</para>
    /// </summary>
    private void OnSamplesAvailable(float[] buffer, int count)
    {
        if (count <= 0 || buffer.Length == 0) return;

        lock (_callbackLock)
        {
            var wav = _wav;
            if (wav is null || !_recording) return;

            try
            {
                var usable = Math.Min(count, buffer.Length);
                wav.Write(buffer.AsSpan(0, usable));
                Interlocked.Add(ref _samples, usable);
            }
            catch (Exception ex)
            {
                // 采集线程上不能抛：记原因 + 停止写入（StopAsync 会看到 _writeFailed 并返回失败）。
                _writeFailed = true;
                SetError("写入录音文件失败：" + ex.Message);
                _recording = false;
            }
        }
    }

    // ===================== 内部工具 =====================

    /// <summary>按「样本数 / 采样率」算录音秒数（与结果记录里的算法一致：四舍五入、至少 1 秒）。</summary>
    private static int SecondsOf(long samples, int sampleRate)
    {
        if (samples <= 0 || sampleRate <= 0) return 0;
        var seconds = (double)samples / sampleRate;
        return Math.Max(1, (int)Math.Round(seconds, MidpointRounding.AwayFromZero));
    }

    /// <summary>删掉本次录音的 WAV / MP3 临时文件（字段同时清空，重复调用安全）。</summary>
    private void CleanupTempFiles()
    {
        string? wavPath, mp3Path;
        lock (_stateLock)
        {
            wavPath = _wavPath;
            mp3Path = _mp3Path;
            _wavPath = null;
            _mp3Path = null;
        }
        TryDelete(wavPath);
        TryDelete(mp3Path);
    }

    /// <summary>记录错误并返回 StartAsync 需要的 (false, 中文原因)。</summary>
    private (bool Ok, string Error) Fail(string message)
    {
        SetError(message);
        return (false, message);
    }

    /// <summary>写 LastError + 输出到调试日志（错误只记一次，成功时清空）。</summary>
    private void SetError(string? message)
    {
        _lastError = message;
        if (!string.IsNullOrEmpty(message)) Trace.WriteLine("[语音消息] " + message);
    }

    /// <summary>释放采集器（吞异常：停设备失败不该影响「收尾 / 清理」这条主路径）。</summary>
    private static void TryDispose(IMicrophoneSource? mic)
    {
        if (mic is null) return;
        try { mic.Dispose(); } catch { /* 释放失败交给进程退出兜底 */ }
    }

    /// <summary>
    /// 尽力删除文件：WinRT / 播放器可能短暂持有句柄，最多重试 3 次、每次 20 ms；
    /// 最终失败也不抛（下一次 Start / Cancel 还会再清一次）。
    /// </summary>
    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }
    }
}
