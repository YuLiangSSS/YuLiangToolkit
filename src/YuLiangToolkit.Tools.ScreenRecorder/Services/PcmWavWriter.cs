using System.Buffers.Binary;
using System.Diagnostics;

namespace YuLiangToolkit.Tools.ScreenRecorder.Services;

/// <summary>
/// 录屏用的 WAV 写入器：PCM16、指定采样率、默认立体声，44 字节标准 RIFF/WAVE 头。
///
/// <para><b>为什么手写</b>：录屏只需要「边录边顺序写 + 停止时回填两个长度字段」这一件事，
/// 引 NAudio 之类会为几十行代码拖进一整个音频框架，违背项目「能手写的小轮子就手写」的约定。</para>
///
/// <para><b>两种线程</b>：<see cref="Write"/> 由 WASAPI 采集线程高频调用，<see cref="Complete"/> /
/// <see cref="Dispose"/> 在停止录制的调用方线程执行；所有入口共用一把锁，所以「采集线程正在写」时
/// 调 <see cref="Complete"/> 是安全的（会等这一段写完再回填头并关文件）。</para>
///
/// <para><b>IO 失败不抛异常</b>：目录自动创建；创建 / 写入 / 收尾任何一步失败都只把中文原因记进
/// <see cref="LastError"/>（采集中途磁盘满也不会把异常抛进 UI），调用方通过 <see cref="LastError"/> 判断。</para>
///
/// <para><b>已知边界</b>：RIFF 的 data 长度是 uint（单文件约 4 GB，录屏场景远远够）；只写 PCM16，
/// 不做重采样 / 声道混合（采集层已经统一成立体声 float）。构造后立刻落一个 0 长度的合法头，
/// 所以即使一个样本都没录到，文件也是可读的 44 字节空 WAV。</para>
/// </summary>
public sealed class PcmWavWriter : IDisposable
{
    /// <summary>标准 PCM WAV 头长度（不含任何扩展块）。</summary>
    private const int HeaderSize = 44;

    /// <summary>RIFF 总长度是 uint；data 最多到 uint.MaxValue - 36。</summary>
    private const long MaxDataBytes = uint.MaxValue - 36L;

    private const string LogPrefix = "[录屏音频] ";

    /// <summary>保护 _stream / _dataBytes / _completed / _lastError 的唯一锁。</summary>
    private readonly object _gate = new();

    /// <summary>输出文件；<see cref="Complete"/> 之后置 null。</summary>
    private FileStream? _stream;

    /// <summary>float → short 的临时缓冲（复用，避免每次回调都分配几十 KB）。</summary>
    private byte[] _bytes = Array.Empty<byte>();

    /// <summary>已经写进文件的样本数据字节数（不含头）。</summary>
    private long _dataBytes;

    /// <summary>是否已经回填头并关闭文件（幂等标记）。</summary>
    private bool _completed;

    private string? _lastError;

    /// <summary>
    /// 创建 WAV 文件并先写入占位的 44 字节头。目录不存在会自动创建。
    /// <para>失败（路径非法 / 没权限 / 磁盘满）不抛异常，原因写进 <see cref="LastError"/>，
    /// 之后的 <see cref="Write"/> 会被静默忽略、<see cref="Complete"/> 也不会产生合法文件。</para>
    /// </summary>
    /// <param name="path">输出路径（通常是录制临时目录里的 audio-xxx.wav）。</param>
    /// <param name="sampleRate">设备混音采样率（一般 48000 / 44100），必须 &gt; 0。</param>
    /// <param name="channels">声道数（默认立体声 2；<see cref="Write"/> 按这个数解释 interleaved 数据）。</param>
    public PcmWavWriter(string path, int sampleRate, int channels = 2)
    {
        SampleRate = sampleRate;
        Channels = channels;

        FileStream? stream = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("WAV 输出路径为空");
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "采样率必须大于 0");
            if (channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channels), channels, "声道数必须在 1~8 之间");

            // 录制临时目录可能还没建（调用方一般会建，这里再兜一次，方便单独使用）
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // FileShare.Read：停止后如果 FFmpeg 已经等在那里，不会因为共享冲突读不了
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 16384, FileOptions.None);
            Span<byte> header = stackalloc byte[HeaderSize];
            BuildHeader(header, sampleRate, channels, dataBytes: 0);
            stream.Write(header);
            stream.Flush();

            _stream = stream;
            stream = null; // 已移交，不让下面的 finally 关掉
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            // PathTooLongException / FileNotFoundException 等都是 IOException 的子类，已被覆盖
            SetError("创建 WAV 文件失败：" + ex.Message);
        }
        finally
        {
            if (stream is not null)
            {
                try { stream.Dispose(); } catch { /* 忽略 */ }
            }
        }
    }

    /// <summary>采样率（构造时传入，之后不变）。</summary>
    public int SampleRate { get; }

    /// <summary>声道数（构造时传入，默认 2）。</summary>
    public int Channels { get; }

    /// <summary>最近一次 IO 失败的中文原因（成功时为 null）。</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>
    /// 追加一段 interleaved float 样本（-1~1，按 <see cref="Channels"/> 交错）。
    /// 内部 clamp 到 PCM16 范围（gain 也会夹到 0~2）；NaN / Infinity 按 0 处理。
    /// <para>IO 失败只记录 <see cref="LastError"/>，不抛异常（采集线程上调用，异常会打乱音频流）。</para>
    /// </summary>
    /// <param name="samples">立体声 interleaved float 缓冲（会被当场消费，不保留引用）。</param>
    /// <param name="frames">帧数（一帧 = Channels 个样本）。</param>
    /// <param name="gain">音量增益（1 = 原样；调用方传 0~2，内部夹取）。</param>
    public void Write(float[] samples, int frames, float gain)
    {
        if (samples is null || frames <= 0) return;

        lock (_gate)
        {
            var stream = _stream;
            if (stream is null || _completed) return;

            var channels = Channels;
            var maxFrames = samples.Length / channels; // 防御：调用方给的帧数超过缓冲就截掉
            if (frames > maxFrames) frames = maxFrames;
            if (frames <= 0) return;

            var sampleCount = frames * channels;
            var byteCount = (long)sampleCount * 2;
            if (_dataBytes + byteCount > MaxDataBytes)
            {
                SetErrorLocked("录音文件超过 WAV 格式上限（约 4 GB），已停止写入");
                return;
            }

            var gainValue = float.IsFinite(gain) ? Math.Clamp(gain, 0f, 2f) : 1f;
            EnsureByteBuffer((int)byteCount);
            var buffer = _bytes;
            for (var i = 0; i < sampleCount; i++)
            {
                var value = samples[i] * gainValue;
                if (!float.IsFinite(value)) value = 0f;
                value = Math.Clamp(value, -1f, 1f);
                var scaled = (int)Math.Round(value * 32767.0, MidpointRounding.AwayFromZero);
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                else if (scaled < short.MinValue) scaled = short.MinValue;
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), (short)scaled);
            }

            try
            {
                stream.Write(buffer, 0, (int)byteCount);
                _dataBytes += byteCount;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or ObjectDisposedException)
            {
                // 磁盘满 / 设备断开：记下来并停写，避免每个 100ms 包都刷一次日志 / 异常
                SetErrorLocked("写入 WAV 数据失败：" + ex.Message);
                try { stream.Dispose(); } catch { /* 忽略 */ }
                _stream = null;
            }
        }
    }

    /// <summary>
    /// 收尾：回填 RIFF / data 长度、flush 并关闭文件（幂等：重复调用只有第一次生效）。
    /// <para>即使一个样本都没写，也会产出一个 44 字节的合法空 WAV。</para>
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;

            var stream = _stream;
            _stream = null;
            if (stream is null) return;

            try
            {
                stream.Flush();
                var dataBytes = (uint)Math.Min(_dataBytes, MaxDataBytes);
                Span<byte> header = stackalloc byte[HeaderSize];
                BuildHeader(header, SampleRate, Channels, dataBytes);
                stream.Seek(0, SeekOrigin.Begin);
                stream.Write(header);
                stream.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or ObjectDisposedException)
            {
                SetErrorLocked("收尾 WAV 文件失败：" + ex.Message);
            }
            finally
            {
                // 回填失败（磁盘满 / 文件被删）也不能把句柄泄漏出去
                try { stream.Dispose(); } catch { /* 忽略 */ }
            }
        }
    }

    /// <summary>释放并尽量回填头（= <see cref="Complete"/>；重复调用安全）。</summary>
    public void Dispose() => Complete();

    // ===================== 内部 =====================

    /// <summary>把 44 字节标准 PCM 头写进 <paramref name="header"/>。</summary>
    private static void BuildHeader(Span<byte> header, int sampleRate, int channels, uint dataBytes)
    {
        var blockAlign = (ushort)(channels * 2); // PCM16：声道数 × 2 字节
        var byteRate = (uint)(sampleRate * blockAlign);

        // RIFF 头：'RIFF' + (36 + dataSize) + 'WAVE'
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 36u + dataBytes);
        "WAVE"u8.CopyTo(header[8..]);

        // fmt 块：16 字节，PCM（1），声道数，采样率，字节率，块对齐，位深
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);

        // data 块头
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], dataBytes);
    }

    /// <summary>保证 float→short 的临时缓冲至少能放 <paramref name="bytes"/> 个字节。</summary>
    private void EnsureByteBuffer(int bytes)
    {
        if (_bytes.Length >= bytes) return;
        _bytes = new byte[Math.Max(bytes, Math.Max(4096, _bytes.Length * 2))];
    }

    /// <summary>已在锁内时记录错误（不再重复加锁）。</summary>
    private void SetErrorLocked(string message)
    {
        _lastError = message;
        Trace.WriteLine(LogPrefix + message);
    }

    private void SetError(string message)
    {
        lock (_gate) _lastError = message;
        Trace.WriteLine(LogPrefix + message);
    }
}
