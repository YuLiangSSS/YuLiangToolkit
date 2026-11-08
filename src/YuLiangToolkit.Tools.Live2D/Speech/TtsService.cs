using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace YuLiangToolkit.Tools.Live2D.Speech;

/// <summary>
/// 语音说话：默认用 **微软 Edge 在线神经网络语音**（晓伊 / 晓晓等，软妹音，零第三方包、直接 WebSocket），
/// 在线失败时自动回退到 Windows 自带 TTS（慧慧等）。文本 → 语音 → 解析 WAV + 算唇形包络 →
/// 内存播放（<see cref="MediaPlayer"/>）；播放期间 <see cref="MouthOpen"/> 按播放进度返回 0~1 的口型值，
/// 交给 <c>ModelRuntime.SpeechMouthOpen</c> 驱动模型嘴型。
///
/// <para><b>同步</b>：<see cref="SpeakAsync"/> 在真正开始播放的那一刻回调 <c>onPlaybackStarted</c>，
/// 界面用它同一时刻显示聊天气泡 / 播小动作，保证「声音 + 口型 + 气泡」同时出现。</para>
///
/// <para><b>线程</b>：<see cref="SpeakAsync"/> / <see cref="SynthesizeAsync"/> 建议在 UI 线程调用
/// （MediaPlayer 在 UI 线程创建最稳）；<see cref="StateChanged"/> 可能在后台线程触发，界面订阅时要
/// <c>Ui(...)</c> 切回界面。</para>
///
/// <para><b>隐私</b>：在线语音会把要说的文本发给微软 Edge TTS 服务（仅文本、不含模型/桌面内容）；
/// 合成结果只在内存里，播放完即释放，不写磁盘、不申请麦克风。离线可在语音列表里选「本地」语音。</para>
/// </summary>
public sealed class TtsService : IDisposable
{
    private const string EdgePrefix = "edge:";
    private const string WindowsPrefix = "win:";

    private static readonly object VoiceCacheLock = new();
    private static IReadOnlyList<SpeechVoiceInfo>? _voiceCache;

    private readonly SemaphoreSlim _synthGate = new(1, 1);
    private readonly Dictionary<string, SpeechUtterance> _cache = new(StringComparer.Ordinal);
    private SpeechSynthesizer? _synthesizer;
    private MediaPlayer? _player;
    private InMemoryRandomAccessStream? _stream;
    private SpeechUtterance? _utterance;
    private Stopwatch? _playWatch;
    private volatile bool _speaking;
    private volatile string? _lastError;
    private string? _lastSpokenText;
    private string _lastEngine = "";
    private double _rate = 1.0;
    private double _pitchPercent = 6;
    private double _volume = 0.9;
    private string _voiceId = "";
    private int _speakGeneration;

    /// <summary>语音开始 / 结束 / 出错（可能在后台线程）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>当前是否正在说话。</summary>
    public bool IsSpeaking => _speaking;

    /// <summary>当前口型（0~1）；没在说话时为 0。</summary>
    public float MouthOpen
    {
        get
        {
            if (!_speaking || _utterance is null || _playWatch is null) return 0f;
            var envelope = _utterance.Envelope;
            if (envelope.Length == 0) return 0f;
            var index = (int)(_playWatch.Elapsed.TotalMilliseconds / 20.0);
            if (index < 0) index = 0;
            if (index >= envelope.Length) index = envelope.Length - 1;
            return envelope[index];
        }
    }

    /// <summary>当前播放位置（秒）。</summary>
    public double PositionSeconds => _playWatch?.Elapsed.TotalSeconds ?? 0;

    /// <summary>最近一次错误（空 = 没有错误；在线失败自动回退本地时会写说明）。</summary>
    public string? LastError => _lastError;

    /// <summary>最近一次说的文本。</summary>
    public string? LastSpokenText => _lastSpokenText;

    /// <summary>最近一次实际使用的引擎（"在线晓伊" / "本地慧慧" 等，状态栏显示用）。</summary>
    public string LastEngine => _lastEngine;

    /// <summary>语音 id（空 = 自动选在线少女音；<c>edge:...</c> = 在线；<c>win:...</c> = 本地）。</summary>
    public string VoiceId
    {
        get => _voiceId;
        set => _voiceId = value ?? "";
    }

    /// <summary>语速倍率（0.6 ~ 1.6；越小越慢越软）。</summary>
    public double Rate
    {
        get => _rate;
        set => _rate = Math.Clamp(value, 0.6, 1.6);
    }

    /// <summary>音调偏移百分比（-20 ~ 40；正数更「软妹」）。在线语音会换算成 Hz。</summary>
    public double PitchPercent
    {
        get => _pitchPercent;
        set => _pitchPercent = Math.Clamp(value, -20, 40);
    }

    /// <summary>播放音量（0 ~ 1）。</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            var player = _player;
            if (player is not null) player.Volume = (float)_volume;
        }
    }

    /// <summary>列出可用语音（在线软妹音排最前，本地系统语音附在最后；失败返回空列表）。</summary>
    public static IReadOnlyList<SpeechVoiceInfo> GetVoices()
    {
        lock (VoiceCacheLock)
        {
            if (_voiceCache is not null) return _voiceCache;
            var list = new List<SpeechVoiceInfo>();
            try
            {
                foreach (var voice in EdgeTtsClient.Voices)
                    list.Add(new SpeechVoiceInfo(EdgePrefix + voice.ShortName, voice.DisplayName, "zh-CN", "Female", "在线 · 软妹音"));
            }
            catch
            {
                // Edge 语音列表不可用时只列本地语音
            }

            try
            {
                foreach (var voice in SpeechSynthesizer.AllVoices)
                {
                    list.Add(new SpeechVoiceInfo(
                        WindowsPrefix + voice.Id,
                        voice.DisplayName,
                        voice.Language,
                        voice.Gender.ToString(),
                        "本地"));
                }
            }
            catch
            {
                // 没有本地语音就算了
            }

            _voiceCache = list;
            return _voiceCache;
        }
    }

    /// <summary>把文本合成成 WAV + 唇形包络（不播放；用全局音色 / 语速 / 音调）。</summary>
    public Task<SpeechUtterance?> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
        => SynthesizeAsync(text, null, cancellationToken);

    /// <summary>
    /// 把文本合成成 WAV + 唇形包络（不播放）。<paramref name="tone"/> 为本次的语速 / 音调 / 音量 /
    /// 情感风格覆盖（null = 保持全局设置）。
    /// </summary>
    public async Task<SpeechUtterance?> SynthesizeAsync(string text, SpeechTone? tone,
        CancellationToken cancellationToken = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
        {
            _lastError = "没有要说的文本";
            return null;
        }
        if (text.Length > 500) text = text[..500];

        var effectiveRate = EffectiveRate(tone);
        var effectivePitch = EffectivePitch(tone);
        var cacheKey = BuildCacheKey(VoiceKey(), effectiveRate, effectivePitch, text, tone);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            _lastError = null;
            return cached;
        }

        await _synthGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastError = null;

            // 1) 在线神经网络语音（默认；失败自动回退本地）
            if (WantsOnlineVoice())
            {
                var utterance = await SynthesizeEdgeAsync(text, effectiveRate, effectivePitch, tone, cancellationToken)
                    .ConfigureAwait(false);
                if (utterance is not null)
                {
                    RememberCache(cacheKey, utterance);
                    return utterance;
                }
                _lastError = "在线语音合成失败" + (string.IsNullOrWhiteSpace(_lastError) ? "" : "：" + _lastError) + "，已改用本地语音";
            }

            // 2) 本地 Windows TTS
            var local = await SynthesizeWindowsAsync(text, effectiveRate, effectivePitch, cancellationToken)
                .ConfigureAwait(false);
            if (local is not null) RememberCache(cacheKey, local);
            return local;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _lastError = "语音合成失败：" + ex.Message;
            return null;
        }
        finally
        {
            _synthGate.Release();
        }
    }

    /// <summary>
    /// 语音缓存键（public 便于自检断言：同一句话不同语气不能串音）。
    /// </summary>
    public static string BuildCacheKey(string voiceKey, double effectiveRate, double effectivePitch,
        string text, SpeechTone? tone)
        => voiceKey + "|" + effectiveRate.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
           effectivePitch.ToString("0.##", CultureInfo.InvariantCulture) + "|" +
           (tone?.CacheKey ?? "neutral") + "|" + text;

    /// <summary>全局语速 × 语气倍率（夹 0.5~2.0）。</summary>
    private double EffectiveRate(SpeechTone? tone)
        => Math.Clamp(_rate * (tone?.Rate ?? 1.0), 0.5, 2.0);

    /// <summary>全局音调 + 语气偏移（夹 -50~80）。</summary>
    private double EffectivePitch(SpeechTone? tone)
        => Math.Clamp(_pitchPercent + (tone?.Pitch ?? 0.0), -50, 80);

    /// <summary>全局音量 × 语气音量倍率（夹 0~1）。</summary>
    private double EffectiveVolume(SpeechTone? tone)
        => Math.Clamp(_volume * (tone?.Volume ?? 1.0), 0.0, 1.0);

    private bool WantsOnlineVoice() =>
        _voiceId.Length == 0 || _voiceId.StartsWith(EdgePrefix, StringComparison.OrdinalIgnoreCase);

    private string VoiceKey() => _voiceId;

    private void RememberCache(string key, SpeechUtterance utterance)
    {
        if (_cache.Count >= 12) _cache.Clear(); // 简单上限，预置台词足够用
        _cache[key] = utterance;
    }

    private async Task<SpeechUtterance?> SynthesizeEdgeAsync(string text, double effectiveRate,
        double effectivePitch, SpeechTone? tone, CancellationToken cancellationToken)
    {
        try
        {
            var shortName = _voiceId.StartsWith(EdgePrefix, StringComparison.OrdinalIgnoreCase)
                ? _voiceId[EdgePrefix.Length..]
                : EdgeTtsClient.DefaultShortName;

            // Edge 朗读接口不支持情感风格 / <break>（实测会返回空音频），所以「语气」靠：
            // 音色 + 全局语速音调 + 按句末标点分段合成（每段独立语速 / 音调）+ 段间插静音。
            var segments = tone is null ? null : SpeechToneCatalog.BuildSegments(text, tone);
            var useSegments = segments is { Count: > 1 };

            var parts = new List<short[]>();
            var gaps = new List<int>();
            var sampleRate = 24_000;

            if (!useSegments)
            {
                var one = await SynthesizeEdgeSegmentAsync(text, shortName, effectiveRate, effectivePitch, cancellationToken)
                    .ConfigureAwait(false);
                if (one is null) return null;
                sampleRate = one.Value.SampleRate;
                parts.Add(one.Value.Samples);
            }
            else
            {
                foreach (var segment in segments!)
                {
                    var segmentRate = Math.Clamp(effectiveRate * segment.RateMultiplier, 0.5, 2.0);
                    var segmentPitch = Math.Clamp(effectivePitch + segment.PitchOffset, -50, 80);
                    var one = await SynthesizeEdgeSegmentAsync(
                            segment.Text, shortName, segmentRate, segmentPitch, cancellationToken)
                        .ConfigureAwait(false);
                    if (one is null) return null;
                    if (parts.Count == 0) sampleRate = one.Value.SampleRate;
                    parts.Add(one.Value.Samples);
                    gaps.Add(segment.GapAfterMs > 0 ? (sampleRate * segment.GapAfterMs) / 1000 : 0);
                }
            }

            short[] combined;
            if (parts.Count == 1)
            {
                combined = parts[0];
            }
            else
            {
                var total = 0;
                foreach (var part in parts) total += part.Length;
                foreach (var gap in gaps) total += gap;
                combined = new short[total];
                var position = 0;
                for (var i = 0; i < parts.Count; i++)
                {
                    Array.Copy(parts[i], 0, combined, position, parts[i].Length);
                    position += parts[i].Length;
                    if (i < gaps.Count) position += gaps[i];   // 默认 short 0 = 静音
                }
            }

            var wav = WaveAudio.BuildPcm16Wav(combined, sampleRate);
            var envelope = WaveAudio.BuildEnvelope(combined, sampleRate);
            _lastEngine = "在线" + EdgeTtsClient.GetDisplayName(shortName) + (useSegments ? " · 分段韵律" : "");
            return new SpeechUtterance(text, wav, sampleRate, 1, 16,
                combined.Length / (double)sampleRate, envelope);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _lastError = "在线语音异常：" + ex.Message;
            return null;
        }
    }

    /// <summary>合成一个片段（单段 / 分段都是同一条路），返回 16bit 单声道样本。</summary>
    private async Task<(short[] Samples, int SampleRate)?> SynthesizeEdgeSegmentAsync(
        string text, string shortName, double rateMultiplier, double pitchPercent, CancellationToken cancellationToken)
    {
        var ratePercent = (int)Math.Round((rateMultiplier - 1.0) * 100.0);
        var rate = ratePercent >= 0 ? "+" + ratePercent + "%" : ratePercent + "%";
        var pitchHz = (int)Math.Round(pitchPercent * 1.2);
        var pitch = pitchHz >= 0 ? "+" + pitchHz + "Hz" : pitchHz + "Hz";

        var mp3 = await EdgeTtsClient.Shared.SynthesizeMp3Async(text, shortName, rate, pitch, cancellationToken)
            .ConfigureAwait(false);
        if (mp3 is null || mp3.Length < 512)
        {
            _lastError = "在线语音没有返回音频" +
                         (string.IsNullOrWhiteSpace(EdgeTtsClient.Shared.LastError) ? "" : "：" + EdgeTtsClient.Shared.LastError);
            return null;
        }

        var wav = await Mp3ToWavAsync(mp3).ConfigureAwait(false);
        if (wav is null)
        {
            _lastError = "在线语音转码失败";
            return null;
        }
        if (!WaveAudio.TryParse(wav, out var info, out var error))
        {
            _lastError = "在线语音 WAV 解析失败：" + error;
            return null;
        }
        return (WaveAudio.ReadMonoSamples(wav, info), info.SampleRate);
    }

    private async Task<SpeechUtterance?> SynthesizeWindowsAsync(string text, double effectiveRate,
        double effectivePitch, CancellationToken cancellationToken)
    {
        try
        {
            var synthesizer = _synthesizer ??= new SpeechSynthesizer();
            var voice = ResolveWindowsVoice();
            if (voice is not null)
            {
                try { synthesizer.Voice = voice; } catch { /* 语音被卸载时用默认 */ }
            }

            var ssml = BuildWindowsSsml(text, effectiveRate, effectivePitch);
            using var stream = await synthesizer.SynthesizeSsmlToStreamAsync(ssml).AsTask(cancellationToken).ConfigureAwait(false);
            var size = (int)stream.Size;
            if (size <= 0)
            {
                _lastError = "本地语音合成结果为空";
                return null;
            }
            var bytes = new byte[size];
            stream.Seek(0);
            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(cancellationToken).ConfigureAwait(false);

            if (!WaveAudio.TryParse(bytes, out var info, out var error))
            {
                _lastError = "本地语音 WAV 解析失败：" + error;
                return null;
            }
            var samples = WaveAudio.ReadMonoSamples(bytes, info);
            var envelope = WaveAudio.BuildEnvelope(samples, info.SampleRate);
            _lastEngine = "本地" + (voice?.DisplayName ?? "系统语音");
            return new SpeechUtterance(text, bytes, info.SampleRate, info.Channels, info.BitsPerSample,
                info.DurationSeconds, envelope);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _lastError = "本地语音合成失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>合成并播放一句话（会先停掉上一句）。返回是否成功开始播放。</summary>
    /// <param name="text">要说的文本。</param>
    /// <param name="onPlaybackStarted">真正开始播放的那一刻回调（用来同一时刻显示气泡 / 动作）。</param>
    /// <param name="cancellationToken">取消。</param>
    public Task<bool> SpeakAsync(string text, Action? onPlaybackStarted = null,
        CancellationToken cancellationToken = default)
        => SpeakAsync(text, null, onPlaybackStarted, cancellationToken);

    /// <summary>
    /// 合成并播放一句话（带语气覆盖：情感风格 / 语速 / 音调 / 音量）。返回是否成功开始播放。
    /// </summary>
    public async Task<bool> SpeakAsync(string text, SpeechTone? tone, Action? onPlaybackStarted = null,
        CancellationToken cancellationToken = default)
    {
        StopCore();
        var generation = ++_speakGeneration;
        // 注意：这里故意不 ConfigureAwait(false)，让播放 / 回调回到调用线程（UI 线程）执行
        var utterance = await SynthesizeAsync(text, tone, cancellationToken);
        if (utterance is null || generation != _speakGeneration) return false;

        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(utterance.WaveBytes.AsBuffer()).AsTask(cancellationToken);
            stream.Seek(0);

            var player = new MediaPlayer { Volume = (float)EffectiveVolume(tone) };
            player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
            player.MediaFailed += (_, args) =>
            {
                _lastError = "语音播放失败：" + (args.ErrorMessage ?? "未知原因");
                FinishSpeaking(generation);
            };

            _stream = stream;
            _player = player;
            _utterance = utterance;
            _lastSpokenText = utterance.Text;
            _playWatch = Stopwatch.StartNew();
            _speaking = true;

            // 同一时刻：开始播放 + 回调（气泡 / 小动作） + 状态事件
            player.Play();
            try { onPlaybackStarted?.Invoke(); } catch { /* 界面回调异常不影响播放 */ }
            StateChanged?.Invoke(this, EventArgs.Empty);

            // 播放完成监测：按 WAV 时长收尾（MediaEnded 在某些机器上不触发，轮询更稳）
            _ = Task.Run(async () =>
            {
                var timeout = utterance.DurationSeconds + 3.0;
                while (generation == _speakGeneration)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    if (_playWatch is null) break;
                    if (_playWatch.Elapsed.TotalSeconds >= utterance.DurationSeconds + 0.35) break;
                    if (_playWatch.Elapsed.TotalSeconds > timeout + 5) break;
                }
                FinishSpeaking(generation);
            });
            return true;
        }
        catch (Exception ex)
        {
            _lastError = "语音播放失败：" + ex.Message;
            FinishSpeaking(generation);
            return false;
        }
    }

    /// <summary>停止说话（立即静音并释放音频）。</summary>
    public void Stop()
    {
        _speakGeneration++;
        StopCore();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FinishSpeaking(int generation)
    {
        if (generation != _speakGeneration) return;
        var wasSpeaking = _speaking;
        StopCore();
        if (wasSpeaking) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StopCore()
    {
        _speaking = false;
        _playWatch = null;
        _utterance = null;
        var player = _player;
        _player = null;
        if (player is not null)
        {
            try { player.Pause(); } catch { }
            try { player.Source = null; } catch { }
            try { player.Dispose(); } catch { }
        }
        var stream = _stream;
        _stream = null;
        try { stream?.Dispose(); } catch { }
    }

    private VoiceInformation? ResolveWindowsVoice()
    {
        if (!_voiceId.StartsWith(WindowsPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = _voiceId[WindowsPrefix.Length..];
        try
        {
            return SpeechSynthesizer.AllVoices.FirstOrDefault(voice =>
                       string.Equals(voice.Id, id, StringComparison.Ordinal))
                   ?? SpeechSynthesizer.AllVoices.FirstOrDefault(voice =>
                       voice.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && voice.Gender == VoiceGender.Female)
                   ?? SpeechSynthesizer.AllVoices.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private string BuildWindowsSsml(string text, double effectiveRate, double effectivePitch)
    {
        var language = "zh-CN";
        var voice = ResolveWindowsVoice();
        if (voice is not null && voice.Language.Length > 0) language = voice.Language;
        var rate = Math.Clamp(effectiveRate, 0.5, 2.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var pitchPercent = (int)Math.Round(Math.Clamp(effectivePitch, -50, 80));
        var pitch = (pitchPercent >= 0 ? "+" : "") + pitchPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
        return "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='" + language + "'>" +
               "<prosody rate='" + rate + "' pitch='" + pitch + "'>" + EscapeSsml(text) + "</prosody></speak>";
    }

    private static string EscapeSsml(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&apos;"); break;
                case '\r': break;
                case '\n': builder.Append(' '); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>MP3 → WAV（用 Windows 自带 MediaTranscoder，在内存里转，不写磁盘）。</summary>
    private static async Task<byte[]?> Mp3ToWavAsync(byte[] mp3)
    {
        try
        {
            var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(mp3.AsBuffer());
            input.Seek(0);
            var output = new InMemoryRandomAccessStream();
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            var transcoder = new MediaTranscoder();
            var prepared = await transcoder.PrepareStreamTranscodeAsync(input, output, profile);
            if (!prepared.CanTranscode) return null;
            await prepared.TranscodeAsync().AsTask();
            var size = (int)output.Size;
            if (size <= 0) return null;
            var bytes = new byte[size];
            output.Seek(0);
            await output.ReadAsync(bytes.AsBuffer(), (uint)size, InputStreamOptions.None);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>停止并释放合成器 / 播放器。</summary>
    public void Dispose()
    {
        Stop();
        try { _synthesizer?.Dispose(); } catch { }
        _synthesizer = null;
        try { _synthGate.Dispose(); } catch { }
    }
}
